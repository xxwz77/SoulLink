using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Services
{
    /// <summary>
    /// 逐帧 H.264 编码器的共同表面（供 SessionRecorder 的降级链使用）。
    /// 让 NvencSdkEncoder 与 MfFrameEncoder 可以互相替换，不改调用方。
    /// </summary>
    internal interface IFrameH264Encoder : IDisposable
    {
        string Name { get; }
        string LastError { get; }
        int ConfiguredBitrateKbps { get; }
        int NegotiatedBitrateKbps { get; }
        bool TryInitialize(int width, int height, int fps, int bitrateKbps, int preset, int vbvPercent);
        byte[]? EncodeNv12(byte[] nv12, long timestampUs);
        void RequestKeyFrame();
    }

    /// <summary>
    /// NVENC SDK D3D11 直通编码器（逐帧 Annex-B NAL 输出）。
    ///
    /// ★ 为什么不用 Media Foundation 的 NVIDIA H.264 MFT：
    ///   本机 MF MFT 报 MF_SA_D3D11_AWARE=FALSE（非 D3D11），MF 无法接收 D3D11 纹理，
    ///   GPU 零拷贝在 MF 框架内死路（见 docs/research/GPU-ZERO-COPY-20261123.md §3）。
    ///   本类直接 P/Invoke NVENC SDK（nvEncodeAPI64.dll），走
    ///   NvEncRegisterResource(DIRECTX) → Map → EncodePicture → LockBitstream，
    ///   把 NV12 D3D11 纹理直通给 GPU 编码器（见 tools/nvenc-sdk-probe 实测骨架）。
    ///
    /// ★ 惰性加载（用户优先级：「响应交互/启动速度」第一）：
    ///   · nvEncodeAPI64.dll 只在 TryInitialize 里 LoadLibraryEx，绝不在程序启动路径加载；
    ///   · TryInitialize 只被 SessionRecorder 的后台视频线程（SoulLinkVideo）调用，
    ///     不阻塞 UI 线程；首次真正需要硬编时才创建 D3D11 设备 + OpenEncodeSessionEx。
    ///   · 动态加载、不随程序分发 → 不增体积（顺手收益，非硬约束）。
    ///
    /// ★ 同步模式：enableEncodeAsync=0 + LockBitstream.doNotWait=0，无需事件泵。
    ///   低帧率（5~30fps）单帧提交场景，同步阻塞到 bitstream 就绪完全够用；
    ///   MF 硬件 MFT「必须事件驱动」的坑在 SDK 路径不存在。
    ///
    /// ★ 输入：CPU NV12（与 MfFrameEncoder 一致，调用方不用改）。
    ///   每帧 UpdateSubresource 上传到已注册的 D3D11 NV12 纹理 → 直通 NVENC。
    ///   （完整 GPU 零拷贝 = WGC 纹理 → GPU 着色器转 NV12 → 本类，是后续阶段。）
    ///
    /// ★ 已知边界（v1）：encodeConfig=NULL + preset P1 + ULTRA_LOW_LATENCY，
    ///   码率由驱动预设决定，尚未按 NV_ENC_CONFIG.rcParams 精确控制 CBR/码率/GOP——
    ///   结构体已在下方定义，精确码控是下一步（对应 MATURE-LATENCY-PLAYBOOK §2）。
    /// </summary>
    public sealed class NvencSdkEncoder : IFrameH264Encoder
    {
        // ── NVENC 常量（nvEncodeAPI.h，SDK 13.1）────────────────────────
        const uint NVENCAPI_MAJOR = 13;
        const uint NVENCAPI_MINOR = 1;
        const uint NVENCAPI_VERSION = NVENCAPI_MAJOR | (NVENCAPI_MINOR << 24);
        static uint StructVersion(uint ver) => NVENCAPI_VERSION | (ver << 16) | (0x7u << 28);
        static uint StructVersionEx(uint ver) => StructVersion(ver) | (1u << 31);

        static readonly Guid H264_GUID = new Guid("6BC82762-4E63-4CA4-AA85-1E50F321F6BF");
        static readonly Guid P1_GUID = new Guid("FC0A8D3E-45F8-4CF8-80C7-298871590EBF");
        static readonly Guid P2_GUID = new Guid("F581CFB8-88D6-4381-93F0-DF13F9C27DAB");
        static readonly Guid P3_GUID = new Guid("36850110-3A07-441F-94D5-3670631F91F6");
        static readonly Guid P4_GUID = new Guid("90A7B826-DF06-4862-B9D2-CD6D73A08681");
        static readonly Guid P5_GUID = new Guid("21C6E6B4-297A-4CBA-998F-B6CBDE72ADE3");
        static readonly Guid P6_GUID = new Guid("8E75C279-6299-4AB6-8302-0B215A335CF5");
        static readonly Guid P7_GUID = new Guid("84848C12-6F71-4C13-931B-53E283F57974");
        static readonly Guid PROFILE_AUTOSELECT_GUID = new Guid("BFD6F8E7-233C-4341-8B3E-4818523803F4");

        const int NV_ENC_DEVICE_TYPE_DIRECTX = 0;
        const int NV_ENC_BUFFER_FORMAT_NV12 = 1;
        const int NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0;
        const int NV_ENC_INPUT_IMAGE = 0;
        const int NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY = 3;
        const int NV_ENC_PIC_STRUCT_FRAME = 1;
        const int NV_ENC_PARAMS_RC_CBR = 2;
        const int NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME = 1;
        const int NV_ENC_MV_PRECISION_DEFAULT = 0;
        const int NV_ENC_LEVEL_AUTOSELECT = 0;
        const uint NVENC_INFINITE_GOPLENGTH = 0xffffffff;
        const uint RC_ZERO_REORDER_DELAY_BIT = 0x200;   // rcParams.bitField1 的 bit9
        const uint NV_ENC_PIC_FLAG_FORCEIDR = 0x2;
        const uint NV_ENC_PIC_FLAG_OUTPUT_SPSPPS = 0x4;
        const uint NV_ENC_SUCCESS = 0;

        // ── 结构体（严格对照 nvEncodeAPI.h，Pack=8）─────────────────────

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_QP { public uint qpInterP, qpInterB, qpIntra; }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_RC_PARAMS
        {
            public uint version;
            public int rateControlMode;
            public NV_ENC_QP constQP;
            public uint averageBitRate;
            public uint maxBitRate;
            public uint vbvBufferSize;
            public uint vbvInitialDelay;
            public uint bitField1;
            public NV_ENC_QP minQP;
            public NV_ENC_QP maxQP;
            public NV_ENC_QP initialRCQP;
            public uint temporallayerIdxMask;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] temporalLayerQP;
            public byte targetQuality;
            public byte targetQualityLSB;
            public ushort lookaheadDepth;
            public byte lowDelayKeyFrameScale;
            public sbyte yDcQPIndexOffset;
            public sbyte uDcQPIndexOffset;
            public sbyte vDcQPIndexOffset;
            public int qpMapMode;
            public int multiPass;
            public uint alphaLayerBitrateRatio;
            public sbyte cbQPIndexOffset;
            public sbyte crQPIndexOffset;
            public ushort reserved2;
            public int lookaheadLevel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] viewBitrateRatios;
            public byte reserved3;
            public uint reserved1;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_CONFIG_H264_VUI_PARAMETERS
        {
            public uint overscanInfoPresentFlag, overscanInfo;
            public uint videoSignalTypePresentFlag; public int videoFormat; public uint videoFullRangeFlag;
            public uint colourDescriptionPresentFlag; public int colourPrimaries; public int transferCharacteristics; public int colourMatrix;
            public uint chromaSampleLocationFlag, chromaSampleLocationTop, chromaSampleLocationBot;
            public uint bitstreamRestrictionFlag;
            public uint timingInfoPresentFlag, numUnitInTicks, timeScale;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public uint[] reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_CONFIG_H264
        {
            public uint bitField1;
            public uint level;
            public uint idrPeriod;
            public uint separateColourPlaneFlag;
            public uint disableDeblockingFilterIDC;
            public uint numTemporalLayers;
            public uint spsId;
            public uint ppsId;
            public int adaptiveTransformMode;
            public int fmoMode;
            public int bdirectMode;
            public int entropyCodingMode;
            public int stereoMode;
            public uint intraRefreshPeriod;
            public uint intraRefreshCnt;
            public uint maxNumRefFrames;
            public uint sliceMode;
            public uint sliceModeData;
            public NV_ENC_CONFIG_H264_VUI_PARAMETERS h264VUIParameters;
            public uint ltrNumFrames;
            public uint ltrTrustMode;
            public uint chromaFormatIDC;
            public uint maxTemporalLayers;
            public int useBFramesAsRef;
            public int numRefL0;
            public int numRefL1;
            public int outputBitDepth;
            public int inputBitDepth;
            public int tfLevel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 264)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_CONFIG
        {
            public uint version;
            public Guid profileGUID;
            public uint gopLength;
            public int frameIntervalP;
            public uint monoChromeEncoding;
            public int frameFieldMode;
            public int mvPrecision;
            public NV_ENC_RC_PARAMS rcParams;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1792)] public byte[] encodeCodecConfig;  // union，按 CONFIG_H264 计
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 278)] public uint[] reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_PRESET_CONFIG
        {
            public uint version;
            public uint reserved;
            public NV_ENC_CONFIG presetCfg;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE
        {
            public uint bitField;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public uint[] reserved1;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_INITIALIZE_PARAMS
        {
            public uint version;
            public Guid encodeGUID;
            public Guid presetGUID;
            public uint encodeWidth;
            public uint encodeHeight;
            public uint darWidth;
            public uint darHeight;
            public uint frameRateNum;
            public uint frameRateDen;
            public uint enableEncodeAsync;
            public uint enablePTD;
            public uint bitField1;
            public uint privDataSize;
            public uint reserved0;
            public IntPtr privData;
            public IntPtr encodeConfig;
            public uint maxEncodeWidth;
            public uint maxEncodeHeight;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[] maxMEHintCountsPerBlock;
            public int tuningInfo;
            public int bufferFormat;
            public uint numStateBuffers;
            public int outputStatsLevel;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_CREATE_BITSTREAM_BUFFER
        {
            public uint version;
            public uint size;
            public int memoryHeap;
            public uint reserved0;
            public IntPtr bitstreamBuffer;
            public IntPtr bitstreamBufferPtr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 58)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_REGISTER_RESOURCE
        {
            public uint version;
            public int resourceType;
            public uint width;
            public uint height;
            public uint pitch;
            public uint subResourceIndex;
            public IntPtr resourceToRegister;
            public IntPtr registeredResource;
            public int bufferFormat;
            public int bufferUsage;
            public IntPtr pInputFencePoint;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public uint[] chromaOffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public uint[] chromaOffsetIn;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 244)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 61)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_MAP_INPUT_RESOURCE
        {
            public uint version;
            public uint subResourceIndex;
            public IntPtr inputResource;
            public IntPtr registeredResource;
            public IntPtr mappedResource;
            public int mappedBufferFmt;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 251)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_PIC_PARAMS
        {
            public uint version;
            public uint inputWidth;
            public uint inputHeight;
            public uint inputPitch;
            public uint encodePicFlags;
            public uint frameIdx;
            public ulong inputTimeStamp;
            public ulong inputDuration;
            public IntPtr inputBuffer;
            public IntPtr outputBitstream;
            public IntPtr completionEvent;
            public int bufferFmt;
            public int pictureStruct;
            public int pictureType;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1024)] public byte[] codecPicParams;  // union 256*4
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[] meHintCountsPerBlock;
            public IntPtr meExternalHints;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] public uint[] reserved2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public IntPtr[] reserved5;
            public IntPtr qpDeltaMap;
            public uint qpDeltaMapSize;
            public uint reservedBitFields;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public ushort[] meHintRefPicDist;
            public int diffPicNumHint;
            public IntPtr alphaBuffer;
            public IntPtr meExternalSbHints;
            public uint meSbHintsCount;
            public uint stateBufferIdx;
            public IntPtr outputReconBuffer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 284)] public uint[] reserved3;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 57)] public IntPtr[] reserved6;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_LOCK_BITSTREAM
        {
            public uint version;
            public uint bitField;
            public IntPtr outputBitstream;
            public IntPtr sliceOffsets;
            public uint frameIdx;
            public uint hwEncodeStatus;
            public uint numSlices;
            public uint bitstreamSizeInBytes;
            public ulong outputTimeStamp;
            public ulong outputDuration;
            public IntPtr bitstreamBufferPtr;
            public int pictureType;
            public int pictureStruct;
            public uint frameAvgQP;
            public uint frameSatd;
            public uint ltrFrameIdx;
            public uint ltrFrameBitmap;
            public uint temporalId;
            public uint intraMBCount;
            public uint interMBCount;
            public int averageMVX;
            public int averageMVY;
            public uint alphaLayerSizeInBytes;
            public uint outputStatsPtrSize;
            public uint reserved0;
            public IntPtr outputStatsPtr;
            public uint frameIdxDisplay;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 219)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 63)] public IntPtr[] reserved2;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public uint[] reservedInternal;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS
        {
            public uint version;
            public int deviceType;
            public IntPtr device;
            public IntPtr reserved0;
            public uint apiVersion;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 253)] public uint[] reserved1;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public IntPtr[] reserved2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        struct NV_ENCODE_API_FUNCTION_LIST
        {
            public uint version;
            public uint reserved;
            public IntPtr nvEncOpenEncodeSession;
            public IntPtr nvEncGetEncodeGUIDCount;
            public IntPtr nvEncGetEncodeProfileGUIDCount;
            public IntPtr nvEncGetEncodeProfileGUIDs;
            public IntPtr nvEncGetEncodeGUIDs;
            public IntPtr nvEncGetInputFormatCount;
            public IntPtr nvEncGetInputFormats;
            public IntPtr nvEncGetEncodeCaps;
            public IntPtr nvEncGetEncodePresetCount;
            public IntPtr nvEncGetEncodePresetGUIDs;
            public IntPtr nvEncGetEncodePresetConfig;
            public IntPtr nvEncInitializeEncoder;
            public IntPtr nvEncCreateInputBuffer;
            public IntPtr nvEncDestroyInputBuffer;
            public IntPtr nvEncCreateBitstreamBuffer;
            public IntPtr nvEncDestroyBitstreamBuffer;
            public IntPtr nvEncEncodePicture;
            public IntPtr nvEncLockBitstream;
            public IntPtr nvEncUnlockBitstream;
            public IntPtr nvEncLockInputBuffer;
            public IntPtr nvEncUnlockInputBuffer;
            public IntPtr nvEncGetEncodeStats;
            public IntPtr nvEncGetSequenceParams;
            public IntPtr nvEncRegisterAsyncEvent;
            public IntPtr nvEncUnregisterAsyncEvent;
            public IntPtr nvEncMapInputResource;
            public IntPtr nvEncUnmapInputResource;
            public IntPtr nvEncDestroyEncoder;
            public IntPtr nvEncInvalidateRefFrames;
            public IntPtr nvEncOpenEncodeSessionEx;
            public IntPtr nvEncRegisterResource;
            public IntPtr nvEncUnregisterResource;
            public IntPtr nvEncReconfigureEncoder;
            public IntPtr reserved1;
            public IntPtr nvEncCreateMVBuffer;
            public IntPtr nvEncDestroyMVBuffer;
            public IntPtr nvEncRunMotionEstimationOnly;
            public IntPtr nvEncGetLastErrorString;
            public IntPtr nvEncSetIOCudaStreams;
            public IntPtr nvEncGetEncodePresetConfigEx;
            public IntPtr nvEncGetSequenceParamEx;
            public IntPtr nvEncRestoreEncoderState;
            public IntPtr nvEncLookaheadPicture;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 275)] public IntPtr[] reserved2;
        }

        // ── 委托（__stdcall = NVENCAPI on Windows）───────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateInstanceFn(ref NV_ENCODE_API_FUNCTION_LIST list);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenSessionExFn(ref NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS p, out IntPtr enc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitEncoderFn(IntPtr enc, ref NV_ENC_INITIALIZE_PARAMS p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPresetConfigExFn(IntPtr enc, Guid encodeGuid, Guid presetGuid, int tuningInfo, IntPtr presetConfig);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateBitstreamBufferFn(IntPtr enc, ref NV_ENC_CREATE_BITSTREAM_BUFFER p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int RegisterResourceFn(IntPtr enc, ref NV_ENC_REGISTER_RESOURCE p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapInputResourceFn(IntPtr enc, ref NV_ENC_MAP_INPUT_RESOURCE p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnmapInputResourceFn(IntPtr enc, IntPtr mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EncodePictureFn(IntPtr enc, ref NV_ENC_PIC_PARAMS p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int LockBitstreamFn(IntPtr enc, ref NV_ENC_LOCK_BITSTREAM p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnlockBitstreamFn(IntPtr enc, IntPtr bitstream);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DestroyEncoderFn(IntPtr enc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UnregisterResourceFn(IntPtr enc, IntPtr reg);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int DestroyBitstreamBufferFn(IntPtr enc, IntPtr buf);

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr hFile, uint flags);
        [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr mod, string name);
        [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr mod);

        // ── 状态 ─────────────────────────────────────────────────────────
        private IntPtr _mod, _enc, _bitstream, _registered;
        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private ID3D11Texture2D? _nv12Tex;
        private int _width, _height, _fps;
        private long _frameIndex;
        private bool _firstFrame = true;
        private bool _forceIdr;          // 下一帧强制 IDR（按需关键帧）
        private int _gopLength = 0;      // 0 = 无限 GOP + 按需 IDR；>0 = 固定 GOP
        private int _preset = 3;          // NVENC 预设 1~7（默认 P3 均衡）
        private int _vbvIncreasePercent = 0;  // 关键帧缓冲上调%（对应 Sunshine vbv_percentage_increase）
        private bool _disposed;

        private MapInputResourceFn? _mapRes;
        private UnmapInputResourceFn? _unmapRes;
        private EncodePictureFn? _encodePic;
        private LockBitstreamFn? _lockBs;
        private UnlockBitstreamFn? _unlockBs;
        private DestroyEncoderFn? _destroyEnc;
        private UnregisterResourceFn? _unregRes;
        private DestroyBitstreamBufferFn? _destroyBs;

        public string Name => "NVENC-SDK";
        public string HardwareStatus => "D3D11直通(同步)";
        public int ConfiguredBitrateKbps { get; private set; }
        public int NegotiatedBitrateKbps { get; private set; }
        public string LastError { get; private set; } = "";

        // ★ 零拷贝（阶段B）：暴露设备/上下文/NV12 纹理给 GpuColorConverter，
        //   使转换器产出的 NV12 与本编码器在同一 device 上（避免跨设备注册）。
        internal ID3D11Device? Device => _device;
        internal ID3D11DeviceContext? Context => _context;
        internal ID3D11Texture2D? Nv12InputTexture => _nv12Tex;

        /// <summary>
        /// 惰性初始化：载入 nvEncodeAPI64.dll → D3D11 设备 → 编码会话 → 注册 NV12 纹理。
        /// 只在 SessionRecorder 后台视频线程首次真正需要硬编时调用，不进启动路径、不阻塞 UI。
        /// 任何一步失败都清理现场并返回 false（调用方静默降级）。
        /// </summary>
        /// <summary>按预设号取 GUID（P1~P7）。越界回落到 P3。</summary>
        private static Guid PresetGuid(int preset) => preset switch
        {
            1 => P1_GUID, 2 => P2_GUID, 4 => P4_GUID, 5 => P5_GUID, 6 => P6_GUID, 7 => P7_GUID, _ => P3_GUID
        };

        public bool TryInitialize(int width, int height, int fps, int bitrateKbps, int preset, int vbvPercent)
        {
            if (_disposed) return false;
            if (_enc != IntPtr.Zero) return true;   // 已初始化
            LastError = "";

            // preset/VBV 是调用方的参数快照，支持 relay 热更新；不读取进程环境变量。
            _preset = Math.Clamp(preset, 1, 7);
            _vbvIncreasePercent = Math.Clamp(vbvPercent, 0, 200);
            if (width <= 0 || height <= 0) { LastError = "帧尺寸无效"; return false; }
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;
            if (width <= 0 || height <= 0) { LastError = "帧尺寸无效"; return false; }

            try
            {
                _mod = LoadLibraryEx("nvEncodeAPI64.dll", IntPtr.Zero, 0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
                if (_mod == IntPtr.Zero) { LastError = "载入 nvEncodeAPI64.dll 失败(err=" + Marshal.GetLastWin32Error() + ")"; Cleanup(); return false; }

                var createPtr = GetProcAddress(_mod, "NvEncodeAPICreateInstance");
                if (createPtr == IntPtr.Zero) { LastError = "找不到 NvEncodeAPICreateInstance"; Cleanup(); return false; }
                var createInstance = Marshal.GetDelegateForFunctionPointer<CreateInstanceFn>(createPtr);
                var fl = new NV_ENCODE_API_FUNCTION_LIST();
                fl.version = StructVersion(2);
                int hr = createInstance(ref fl);
                if (hr != 0) { LastError = "NvEncodeAPICreateInstance hr=0x" + hr.ToString("X8"); Cleanup(); return false; }

                // D3D11 硬件设备（WARP 不产生显存纹理，零拷贝要求硬件设备）
                var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                var dhr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out _, out var context);
                if (!dhr.Success || device == null) { LastError = "D3D11 硬件设备失败 0x" + dhr.Code.ToString("X8"); Cleanup(); return false; }
                _device = device; _context = context;

                // 编码会话（DIRECTX 设备直通）
                var openSession = Marshal.GetDelegateForFunctionPointer<OpenSessionExFn>(fl.nvEncOpenEncodeSessionEx);
                var ose = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS();
                ose.version = StructVersion(1);
                ose.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
                ose.device = device.NativePointer;
                ose.apiVersion = NVENCAPI_VERSION;
                hr = openSession(ref ose, out _enc);
                if (hr != 0) { LastError = "NvEncOpenEncodeSessionEx(DIRECTX) hr=0x" + hr.ToString("X8"); Cleanup(); return false; }

                // 初始化（同步模式 + 编码器决定帧型 + 低延迟预设；encodeConfig=NULL 走 P1 预设）
                var initEnc = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(fl.nvEncInitializeEncoder);
                // 精确码控：先 GetEncodePresetConfigEx 取合法预设，再覆盖 rcParams（Sunshine 标准做法）
                // ★ codec union 由 preset 决定，不能单独改（GetEncodePresetConfigEx 返回的 union 未填充，
                //   改任何字段都 INVALID_PARAM，实测确认）。按需 IDR 靠 EncodeNv12 里的 FORCEIDR|OUTPUT_SPSPPS。
                var getPreset = Marshal.GetDelegateForFunctionPointer<GetPresetConfigExFn>(fl.nvEncGetEncodePresetConfigEx);
                var presetGuid = PresetGuid(_preset);
                var pc = new NV_ENC_PRESET_CONFIG();
                pc.version = StructVersionEx(5);
                pc.presetCfg.version = StructVersionEx(9);   // NV_ENC_CONFIG_VER
                pc.presetCfg.encodeCodecConfig = new byte[1792];
                pc.presetCfg.reserved = new uint[278];
                pc.presetCfg.reserved2 = new IntPtr[64];
                pc.presetCfg.rcParams.temporalLayerQP = new byte[8];
                pc.presetCfg.rcParams.viewBitrateRatios = new byte[8];
                IntPtr pcPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NV_ENC_PRESET_CONFIG>());
                try
                {
                    Marshal.StructureToPtr(pc, pcPtr, false);
                    hr = getPreset(_enc, H264_GUID, presetGuid, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, pcPtr);
                    if (hr == 0) pc = (NV_ENC_PRESET_CONFIG)Marshal.PtrToStructure(pcPtr, typeof(NV_ENC_PRESET_CONFIG));
                }
                finally { Marshal.FreeHGlobal(pcPtr); }
                if (hr != 0) { LastError = "GetEncodePresetConfigEx hr=0x" + hr.ToString("X8"); Cleanup(); return false; }

                var cfg = pc.presetCfg;
                cfg.profileGUID = PROFILE_AUTOSELECT_GUID;
                cfg.gopLength = _gopLength > 0 ? (uint)_gopLength : NVENC_INFINITE_GOPLENGTH;  // 无限 GOP + 按需 IDR
                cfg.frameIntervalP = 1;                        // 关 B 帧
                cfg.rcParams.rateControlMode = NV_ENC_PARAMS_RC_CBR;
                cfg.rcParams.averageBitRate = (uint)Math.Max(500, bitrateKbps) * 1000;
                cfg.rcParams.maxBitRate = cfg.rcParams.averageBitRate;
                cfg.rcParams.bitField1 |= RC_ZERO_REORDER_DELAY_BIT;   // zeroReorderDelay=1（低延迟无重排）
                cfg.rcParams.lowDelayKeyFrameScale = 1;
                cfg.rcParams.multiPass = 0;                   // 单 pass
                cfg.rcParams.vbvBufferSize = cfg.rcParams.averageBitRate / (uint)Math.Max(1, fps);  // 1 帧 bit 量（Sunshine: bitrate*1000/fps）
                if (_vbvIncreasePercent > 0)
                    cfg.rcParams.vbvBufferSize += (uint)((ulong)cfg.rcParams.vbvBufferSize * (uint)_vbvIncreasePercent / 100u);  // 关键帧缓冲上调%

                var init = new NV_ENC_INITIALIZE_PARAMS();
                init.maxMEHintCountsPerBlock = new NVENC_EXTERNAL_ME_HINT_COUNTS_PER_BLOCKTYPE[2];
                init.reserved1 = new uint[284];
                init.reserved2 = new IntPtr[64];
                init.version = StructVersionEx(7);
                init.encodeGUID = H264_GUID;
                init.presetGUID = presetGuid;
                init.encodeWidth = (uint)width; init.encodeHeight = (uint)height;
                init.darWidth = (uint)width; init.darHeight = (uint)height;
                init.frameRateNum = (uint)Math.Max(1, fps); init.frameRateDen = 1;
                init.enableEncodeAsync = 0;   // 同步
                init.enablePTD = 1;           // 编码器决定帧型，用 FORCEIDR 强制关键帧
                init.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;

                IntPtr cfgPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NV_ENC_CONFIG>());
                try
                {
                    Marshal.StructureToPtr(cfg, cfgPtr, false);
                    init.encodeConfig = cfgPtr;
                    hr = initEnc(_enc, ref init);
                }
                finally { Marshal.FreeHGlobal(cfgPtr); }
                if (hr != 0) { LastError = "NvEncInitializeEncoder(rcParams) hr=0x" + hr.ToString("X8"); Cleanup(); return false; }

                // 输出 bitstream 缓冲
                var createBs = Marshal.GetDelegateForFunctionPointer<CreateBitstreamBufferFn>(fl.nvEncCreateBitstreamBuffer);
                var cbb = new NV_ENC_CREATE_BITSTREAM_BUFFER();
                cbb.version = StructVersion(1);
                hr = createBs(_enc, ref cbb);
                if (hr != 0) { LastError = "NvEncCreateBitstreamBuffer hr=0x" + hr.ToString("X8"); Cleanup(); return false; }
                _bitstream = cbb.bitstreamBuffer;

                // 造 + 注册 NV12 D3D11 纹理（DIRECTX 直通的核心）
                if (!CreateNv12Texture(width, height)) { Cleanup(); return false; }
                var regRes = Marshal.GetDelegateForFunctionPointer<RegisterResourceFn>(fl.nvEncRegisterResource);
                var rr = new NV_ENC_REGISTER_RESOURCE();
                rr.version = StructVersion(5);
                rr.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
                rr.width = (uint)width; rr.height = (uint)height;
                rr.pitch = 0;
                rr.subResourceIndex = 0;
                rr.resourceToRegister = _nv12Tex!.NativePointer;
                rr.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
                rr.bufferUsage = NV_ENC_INPUT_IMAGE;
                hr = regRes(_enc, ref rr);
                if (hr != 0) { LastError = "NvEncRegisterResource(DIRECTX,NV12) hr=0x" + hr.ToString("X8"); Cleanup(); return false; }
                _registered = rr.registeredResource;

                // 缓存每帧委托
                _mapRes = Marshal.GetDelegateForFunctionPointer<MapInputResourceFn>(fl.nvEncMapInputResource);
                _unmapRes = Marshal.GetDelegateForFunctionPointer<UnmapInputResourceFn>(fl.nvEncUnmapInputResource);
                _encodePic = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(fl.nvEncEncodePicture);
                _lockBs = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(fl.nvEncLockBitstream);
                _unlockBs = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(fl.nvEncUnlockBitstream);
                _destroyEnc = Marshal.GetDelegateForFunctionPointer<DestroyEncoderFn>(fl.nvEncDestroyEncoder);
                _unregRes = Marshal.GetDelegateForFunctionPointer<UnregisterResourceFn>(fl.nvEncUnregisterResource);
                _destroyBs = Marshal.GetDelegateForFunctionPointer<DestroyBitstreamBufferFn>(fl.nvEncDestroyBitstreamBuffer);

                _width = width; _height = height; _fps = Math.Max(1, fps);
                ConfiguredBitrateKbps = Math.Max(500, bitrateKbps);
                // v1 用 P1 预设默认码率；精确 CBR 码控需 NV_ENC_CONFIG.rcParams（下一步）。
                NegotiatedBitrateKbps = ConfiguredBitrateKbps;
                _frameIndex = 0; _firstFrame = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                Cleanup();
                return false;
            }
        }

        /// <summary>
        /// 编码一帧 CPU NV12，产出 Annex-B NAL（与 MfFrameEncoder 输出格式一致）。
        /// 返回 null 表示本帧无输出；异常表示编码器故障（调用方应降级）。
        /// </summary>
        public byte[]? EncodeNv12(byte[] nv12, long timestampUs)
        {
            if (_disposed || _enc == IntPtr.Zero || _nv12Tex == null || _context == null) return null;
            if (nv12 == null || nv12.Length == 0) return null;
            if (_mapRes == null || _encodePic == null || _lockBs == null) return null;

            int ySize = _width * _height;
            int total = ySize + ySize / 2;
            if (nv12.Length < total) return null;

            try
            {
                // CPU NV12 → 已注册 D3D11 纹理（每帧更新）
                IntPtr p = Marshal.AllocHGlobal(total);
                Marshal.Copy(nv12, 0, p, total);
                _context.UpdateSubresource(_nv12Tex, 0, null, p, (uint)_width, (uint)ySize);
                Marshal.FreeHGlobal(p);

                var map = new NV_ENC_MAP_INPUT_RESOURCE();
                map.version = StructVersion(4);
                map.registeredResource = _registered;
                int hr = _mapRes(_enc, ref map);
                if (hr != 0) throw new InvalidOperationException("NvEncMapInputResource hr=0x" + hr.ToString("X8"));

                try
                {
                    var pic = new NV_ENC_PIC_PARAMS();
                    pic.version = StructVersionEx(7);
                    pic.inputWidth = (uint)_width; pic.inputHeight = (uint)_height;
                    pic.inputPitch = (uint)_width;
                    // 按需 IDR：首帧 / 显式 RequestKeyFrame / 新观众接入时强制 IDR
                    bool forceIdr = _firstFrame || _forceIdr;
                    pic.encodePicFlags = forceIdr ? (NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS) : 0u;
                    pic.frameIdx = (uint)_frameIndex;
                    pic.inputTimeStamp = (ulong)timestampUs;
                    pic.inputDuration = 10_000_000L / (ulong)_fps;
                    pic.inputBuffer = map.mappedResource;
                    pic.outputBitstream = _bitstream;
                    pic.completionEvent = IntPtr.Zero;
                    pic.bufferFmt = map.mappedBufferFmt;
                    pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
                    hr = _encodePic(_enc, ref pic);
                    if (hr != 0) throw new InvalidOperationException("NvEncEncodePicture hr=0x" + hr.ToString("X8"));

                    var lb = new NV_ENC_LOCK_BITSTREAM();
                    lb.version = StructVersionEx(2);
                    lb.outputBitstream = _bitstream;
                    lb.bitField = 0;   // doNotWait=0，阻塞到编码完成
                    hr = _lockBs(_enc, ref lb);
                    if (hr != 0) throw new InvalidOperationException("NvEncLockBitstream hr=0x" + hr.ToString("X8"));

                    byte[] nal = new byte[lb.bitstreamSizeInBytes];
                    if (nal.Length > 0) Marshal.Copy(lb.bitstreamBufferPtr, nal, 0, nal.Length);
                    _unlockBs!(_enc, _bitstream);
                    _forceIdr = false;
                    _firstFrame = false;
                    _frameIndex++;
                    return nal.Length > 0 ? nal : null;
                }
                finally { _unmapRes!(_enc, map.mappedResource); }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                throw;
            }
        }

        /// <summary>
        /// 编码一帧「已写入 _nv12Tex 的 GPU NV12 纹理」，跳过 UpdateSubresource 上传段（零拷贝）。
        /// 传入的 nv12Tex 必须就是本编码器注册的 Nv12InputTexture（GpuColorConverter 写的就是它）；
        /// 不符则抛异常（接线错误，不应发生）。返回 null = 无输出；异常 = 编码器故障（调用方降级）。
        /// </summary>
        public byte[]? EncodeD3D11Texture(ID3D11Texture2D nv12Tex, long timestampUs)
        {
            if (_disposed || _enc == IntPtr.Zero || _nv12Tex == null || _context == null) return null;
            if (_mapRes == null || _encodePic == null || _lockBs == null) return null;
            if (!ReferenceEquals(nv12Tex, _nv12Tex))
                throw new InvalidOperationException("EncodeD3D11Texture 收到的纹理不是已注册的 Nv12InputTexture（接线错误）");

            try
            {
                // ★ 零拷贝关键：不做 UpdateSubresource（像素已在 GPU 上，由 VideoProcessor 写入 _nv12Tex），
                //   直接 Map 已注册纹理 → Encode → Lock。
                var map = new NV_ENC_MAP_INPUT_RESOURCE();
                map.version = StructVersion(4);
                map.registeredResource = _registered;
                int hr = _mapRes(_enc, ref map);
                if (hr != 0) throw new InvalidOperationException("NvEncMapInputResource hr=0x" + hr.ToString("X8"));

                try
                {
                    var pic = new NV_ENC_PIC_PARAMS();
                    pic.version = StructVersionEx(7);
                    pic.inputWidth = (uint)_width; pic.inputHeight = (uint)_height;
                    pic.inputPitch = (uint)_width;
                    bool forceIdr = _firstFrame || _forceIdr;
                    pic.encodePicFlags = forceIdr ? (NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS) : 0u;
                    pic.frameIdx = (uint)_frameIndex;
                    pic.inputTimeStamp = (ulong)timestampUs;
                    pic.inputDuration = 10_000_000L / (ulong)_fps;
                    pic.inputBuffer = map.mappedResource;
                    pic.outputBitstream = _bitstream;
                    pic.completionEvent = IntPtr.Zero;
                    pic.bufferFmt = map.mappedBufferFmt;
                    pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
                    hr = _encodePic(_enc, ref pic);
                    if (hr != 0) throw new InvalidOperationException("NvEncEncodePicture hr=0x" + hr.ToString("X8"));

                    var lb = new NV_ENC_LOCK_BITSTREAM();
                    lb.version = StructVersionEx(2);
                    lb.outputBitstream = _bitstream;
                    lb.bitField = 0;
                    hr = _lockBs(_enc, ref lb);
                    if (hr != 0) throw new InvalidOperationException("NvEncLockBitstream hr=0x" + hr.ToString("X8"));

                    byte[] nal = new byte[lb.bitstreamSizeInBytes];
                    if (nal.Length > 0) Marshal.Copy(lb.bitstreamBufferPtr, nal, 0, nal.Length);
                    _unlockBs!(_enc, _bitstream);
                    _forceIdr = false;
                    _firstFrame = false;
                    _frameIndex++;
                    return nal.Length > 0 ? nal : null;
                }
                finally { _unmapRes!(_enc, map.mappedResource); }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                throw;
            }
        }

        /// <summary>请求下一帧为关键帧（按需 IDR）。新观众接入/丢帧重同步时调用。</summary>
        public void RequestKeyFrame() => _forceIdr = true;

        private bool CreateNv12Texture(int w, int h)
        {
            try
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    // ★ 零拷贝：BindFlags=RenderTarget 让 VideoProcessor 能写此纹理（BGRA→NV12）。
                    //   UpdateSubresource（CPU 路径）不要求 BindFlags，此改动不影响现状 CPU 上传。
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
                _nv12Tex = _device!.CreateTexture2D(desc);
                return true;
            }
            catch (Exception ex) { LastError = "创建 NV12 纹理失败：" + ex.Message; return false; }
        }

        private void Cleanup()
        {
            try { if (_enc != IntPtr.Zero && _registered != IntPtr.Zero && _unregRes != null) _unregRes(_enc, _registered); } catch { }
            try { if (_enc != IntPtr.Zero && _bitstream != IntPtr.Zero && _destroyBs != null) _destroyBs(_enc, _bitstream); } catch { }
            try { if (_enc != IntPtr.Zero && _destroyEnc != null) _destroyEnc(_enc); } catch { }
            try { _nv12Tex?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            try { if (_mod != IntPtr.Zero) FreeLibrary(_mod); } catch { }
            _nv12Tex = null; _context = null; _device = null;
            _enc = IntPtr.Zero; _registered = IntPtr.Zero; _bitstream = IntPtr.Zero; _mod = IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }
    }
}
