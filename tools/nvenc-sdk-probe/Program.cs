// nvenc-sdk-probe: 验证 RTX 4060 能否走 NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX 直通出帧。
// 纯独立探针，不依赖主项目。只读系统 NVENC 驱动 DLL + 建自己的 D3D11 设备/纹理。
// 结构体布局严格对照 nvEncodeAPI.h（NVENC SDK 13.1，MSVC 8 字节默认对齐）。

using System;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace NvencSdkProbe
{
    internal static class Program
    {
        // ── NVENC 常量（nvEncodeAPI.h）──────────────────────────────
        const uint NVENCAPI_MAJOR = 13;
        const uint NVENCAPI_MINOR = 1;
        const uint NVENCAPI_VERSION = NVENCAPI_MAJOR | (NVENCAPI_MINOR << 24);
        static uint StructVersion(uint ver) => NVENCAPI_VERSION | (ver << 16) | (0x7u << 28);
        static uint StructVersionEx(uint ver) => StructVersion(ver) | (1u << 31);

        static readonly Guid H264_GUID = new Guid("6BC82762-4E63-4CA4-AA85-1E50F321F6BF");
        static readonly Guid P1_GUID   = new Guid("FC0A8D3E-45F8-4CF8-80C7-298871590EBF");
        static readonly Guid P2_GUID   = new Guid("F581CFB8-88D6-4381-93F0-DF13F9C27DAB");
        static readonly Guid P3_GUID   = new Guid("36850110-3A07-441F-94D5-3670631F91F6");
        static readonly Guid P4_GUID   = new Guid("90A7B826-DF06-4862-B9D2-CD6D73A08681");
        static readonly Guid P5_GUID   = new Guid("21C6E6B4-297A-4CBA-998F-B6CBDE72ADE3");
        static readonly Guid P6_GUID   = new Guid("8E75C279-6299-4AB6-8302-0B215A335CF5");
        static readonly Guid P7_GUID   = new Guid("84848C12-6F71-4C13-931B-53E283F57974");
        static readonly Guid PROFILE_AUTOSELECT_GUID = new Guid("BFD6F8E7-233C-4341-8B3E-4818523803F4");
        static readonly Guid H264_PROFILE_HIGH_GUID = new Guid("E7CBC309-4F7A-4B89-AF2A-D537C92BE310");

        const int NV_ENC_DEVICE_TYPE_DIRECTX = 0;
        const int NV_ENC_BUFFER_FORMAT_NV12 = 1;
        const int NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0;
        const int NV_ENC_INPUT_IMAGE = 0;
        const int NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY = 3;
        const int NV_ENC_PIC_STRUCT_FRAME = 1;
        const uint NV_ENC_PIC_FLAG_FORCEIDR = 0x2;
        const uint NV_ENC_PIC_FLAG_OUTPUT_SPSPPS = 0x4;
        const uint NV_ENC_SUCCESS = 0;
        const int NV_ENC_PARAMS_RC_CBR = 2;
        const int NV_ENC_PARAMS_FRAME_FIELD_MODE_FRAME = 1;
        const int NV_ENC_MV_PRECISION_DEFAULT = 0;
        const int NV_ENC_LEVEL_AUTOSELECT = 0;
        const uint NVENC_INFINITE_GOPLENGTH = 0xffffffff;
        const uint RC_ZERO_REORDER_DELAY_BIT = 0x200;   // rcParams.bitField1 的 bit9

        // ── 结构体（严格对照 nvEncodeAPI.h，Pack=8）───────────────────

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
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1792)] public byte[] encodeCodecConfig;  // union, 按 CONFIG_H264 计
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 278)] public uint[] reserved;
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
        struct NV_ENC_PRESET_CONFIG
        {
            public uint version;
            public uint reserved;
            public NV_ENC_CONFIG presetCfg;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public uint[] reserved1;
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

        // ── 委托（__stdcall = NVENCAPI on Windows）─────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateInstanceFn(ref NV_ENCODE_API_FUNCTION_LIST list);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetMaxVersionFn(out uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenSessionExFn(ref NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS p, out IntPtr enc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetEncodeGUIDsFn(IntPtr enc, IntPtr guids, uint size, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetPresetConfigExFn(IntPtr enc, Guid encodeGuid, Guid presetGuid, int tuningInfo, IntPtr presetConfig);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int InitEncoderFn(IntPtr enc, ref NV_ENC_INITIALIZE_PARAMS p);
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

        // 静态状态（供 Cleanup 使用）
        static IntPtr _mod;
        static IntPtr _enc;
        static IntPtr _bitstream;
        static IntPtr _registered;
        static ID3D11Device _device;
        static ID3D11DeviceContext _context;
        static ID3D11Texture2D _nv12Tex;
        static bool _useRcParams;
        static int _bitrateKbps = 8000;
        static int _gopLength;
        static int _preset = 3;
        static int _vbvPct = 0;
        static DestroyEncoderFn _destroyEncoder;
        static UnregisterResourceFn _unregisterResource;
        static DestroyBitstreamBufferFn _destroyBitstream;
        static UnmapInputResourceFn _unmapResource;

        static int Main(string[] args)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            int frames = 3;
            string outPath = Path.Combine(Path.GetTempPath(), "nvenc-sdk-probe.h264");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-N" && i + 1 < args.Length) int.TryParse(args[++i], out frames);
                else if (args[i] == "-Out" && i + 1 < args.Length) outPath = args[++i];
                else if (args[i] == "-R") _useRcParams = true;   // 启用精确 rcParams
                else if (args[i] == "-B" && i + 1 < args.Length) int.TryParse(args[++i], out _bitrateKbps);
                else if (args[i] == "-G" && i + 1 < args.Length) int.TryParse(args[++i], out _gopLength);
                else if (args[i] == "-P" && i + 1 < args.Length) int.TryParse(args[++i], out _preset);
                else if (args[i] == "-V" && i + 1 < args.Length) int.TryParse(args[++i], out _vbvPct);
            }

            Log("════ NVENC SDK D3D11 直通探针 ════");
            Log("进程 SessionId=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
            Log("NVENC SDK header 版本 " + NVENCAPI_MAJOR + "." + NVENCAPI_MINOR);
            try
            {
                Log("offsetof 校验：cfg.version=" + Marshal.OffsetOf<NV_ENC_CONFIG>("version")
                    + " profileGUID=" + Marshal.OffsetOf<NV_ENC_CONFIG>("profileGUID")
                    + " gopLength=" + Marshal.OffsetOf<NV_ENC_CONFIG>("gopLength")
                    + " frameIntervalP=" + Marshal.OffsetOf<NV_ENC_CONFIG>("frameIntervalP")
                    + " rcParams=" + Marshal.OffsetOf<NV_ENC_CONFIG>("rcParams")
                    + " encodeCodecConfig=" + Marshal.OffsetOf<NV_ENC_CONFIG>("encodeCodecConfig")
                    + " reserved=" + Marshal.OffsetOf<NV_ENC_CONFIG>("reserved")
                    + " reserved2=" + Marshal.OffsetOf<NV_ENC_CONFIG>("reserved2"));
                Log("offsetof rcParams：version=" + Marshal.OffsetOf<NV_ENC_RC_PARAMS>("version")
                    + " rateControlMode=" + Marshal.OffsetOf<NV_ENC_RC_PARAMS>("rateControlMode")
                    + " averageBitRate=" + Marshal.OffsetOf<NV_ENC_RC_PARAMS>("averageBitRate")
                    + " bitField1=" + Marshal.OffsetOf<NV_ENC_RC_PARAMS>("bitField1"));
                Log("offsetof init：encodeGUID=" + Marshal.OffsetOf<NV_ENC_INITIALIZE_PARAMS>("encodeGUID")
                    + " encodeWidth=" + Marshal.OffsetOf<NV_ENC_INITIALIZE_PARAMS>("encodeWidth")
                    + " encodeConfig=" + Marshal.OffsetOf<NV_ENC_INITIALIZE_PARAMS>("encodeConfig")
                    + " tuningInfo=" + Marshal.OffsetOf<NV_ENC_INITIALIZE_PARAMS>("tuningInfo"));
            }
            catch (Exception ex) { Log("offsetof 失败：" + ex.Message); }

            Log("sizeof 校验：RC=" + Marshal.SizeOf<NV_ENC_RC_PARAMS>()
                + " H264=" + Marshal.SizeOf<NV_ENC_CONFIG_H264>()
                + " CONFIG=" + Marshal.SizeOf<NV_ENC_CONFIG>()
                + " INIT=" + Marshal.SizeOf<NV_ENC_INITIALIZE_PARAMS>()
                + " PIC=" + Marshal.SizeOf<NV_ENC_PIC_PARAMS>()
                + " LOCK=" + Marshal.SizeOf<NV_ENC_LOCK_BITSTREAM>()
                + " REG=" + Marshal.SizeOf<NV_ENC_REGISTER_RESOURCE>()
                + " MAP=" + Marshal.SizeOf<NV_ENC_MAP_INPUT_RESOURCE>()
                + " CBB=" + Marshal.SizeOf<NV_ENC_CREATE_BITSTREAM_BUFFER>()
                + " FL=" + Marshal.SizeOf<NV_ENCODE_API_FUNCTION_LIST>());

            try { return Run(frames, outPath); }
            catch (Exception ex) { Log("[FAIL] 未处理异常：" + ex.GetType().Name + " " + ex.Message); return 2; }
            finally { Cleanup(); }
        }

        static int Run(int frames, string outPath)
        {
            _mod = LoadLibraryEx("nvEncodeAPI64.dll", IntPtr.Zero, 0x00000800);
            if (_mod == IntPtr.Zero) { Log("[FAIL] 载入 nvEncodeAPI64.dll 失败 err=" + Marshal.GetLastWin32Error()); return 1; }
            Log("[OK  ] nvEncodeAPI64.dll 载入");

            var maxPtr = GetProcAddress(_mod, "NvEncodeAPIGetMaxSupportedVersion");
            if (maxPtr != IntPtr.Zero)
            {
                var fn = Marshal.GetDelegateForFunctionPointer<GetMaxVersionFn>(maxPtr);
                fn(out uint mv);
                Log("[INFO] MaxSupportedVersion=0x" + mv.ToString("X8") + "（主 " + (mv & 0xFF) + " 次 " + ((mv >> 24) & 0xFF) + "）");
            }

            var createPtr = GetProcAddress(_mod, "NvEncodeAPICreateInstance");
            if (createPtr == IntPtr.Zero) { Log("[FAIL] 找不到 NvEncodeAPICreateInstance"); return 1; }
            var createInstance = Marshal.GetDelegateForFunctionPointer<CreateInstanceFn>(createPtr);
            var fl = new NV_ENCODE_API_FUNCTION_LIST();
            fl.version = StructVersion(2);
            int hr = createInstance(ref fl);
            if (hr != 0) { Log("[FAIL] NvEncodeAPICreateInstance hr=0x" + hr.ToString("X8")); return 1; }
            Log("[OK  ] NvEncodeAPICreateInstance 成功");

            // D3D11 硬件设备
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            var dhr = Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var device, out var featLevel, out var context);
            if (!dhr.Success || device == null) { Log("[FAIL] D3D11 硬件设备失败 0x" + dhr.Code.ToString("X8")); return 1; }
            _device = device; _context = context;
            Log("[OK  ] D3D11 硬件设备 featureLevel=" + featLevel);

            // OpenEncodeSessionEx(DIRECTX)
            var openSession = Marshal.GetDelegateForFunctionPointer<OpenSessionExFn>(fl.nvEncOpenEncodeSessionEx);
            var ose = new NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS();
            ose.version = StructVersion(1);
            ose.deviceType = NV_ENC_DEVICE_TYPE_DIRECTX;
            ose.device = device.NativePointer;
            ose.apiVersion = NVENCAPI_VERSION;
            hr = openSession(ref ose, out _enc);
            if (hr != 0) { Log("[FAIL] NvEncOpenEncodeSessionEx(DIRECTX) hr=0x" + hr.ToString("X8")); return 1; }
            Log("[OK  ] NvEncOpenEncodeSessionEx(DIRECTX) 成功");

            // 枚举 GUID
            var getGuids = Marshal.GetDelegateForFunctionPointer<GetEncodeGUIDsFn>(fl.nvEncGetEncodeGUIDs);
            IntPtr gbuf = Marshal.AllocHGlobal(16 * 8);
            hr = getGuids(_enc, gbuf, 8, out uint gcount);
            bool hasH264 = false;
            Log("[INFO] GetEncodeGUIDs hr=0x" + hr.ToString("X8") + " count=" + gcount);
            byte[] graw = new byte[16 * Math.Max(1, gcount)];
            Marshal.Copy(gbuf, graw, 0, graw.Length);
            for (int i = 0; i < gcount; i++)
            {
                var g = new Guid(graw.AsSpan(i * 16, 16));
                Log("[INFO] 编码 GUID[" + i + "]=" + g + (g == H264_GUID ? " ← H.264" : ""));
                if (g == H264_GUID) hasH264 = true;
            }
            Marshal.FreeHGlobal(gbuf);
            Log(hasH264 ? "[OK  ] 支持 H.264" : "[WARN] 未枚举到 H.264 GUID");

            // 造 NV12 纹理
            int W = 1920, H = 1080;
            if (!CreateNv12Texture(W, H)) { Log("[FAIL] 创建 NV12 纹理失败"); return 1; }
            Log("[OK  ] 创建 1920x1080 NV12 D3D11 纹理（DEFAULT + UpdateSubresource 填充）");

            // 初始化编码器
            var initEnc = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(fl.nvEncInitializeEncoder);
            var init = new NV_ENC_INITIALIZE_PARAMS();
            init.version = StructVersionEx(7);
            init.encodeGUID = H264_GUID;
            init.presetGUID = P1_GUID;
            init.encodeWidth = (uint)W; init.encodeHeight = (uint)H;
            init.darWidth = (uint)W; init.darHeight = (uint)H;
            init.frameRateNum = 30; init.frameRateDen = 1;
            init.enableEncodeAsync = 0;
            init.enablePTD = 1;
            init.tuningInfo = NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY;
            if (_useRcParams)
            {
                // Sunshine 标准做法：先 GetEncodePresetConfigEx 取合法预设，再改码控/编码字段
                var getPreset = Marshal.GetDelegateForFunctionPointer<GetPresetConfigExFn>(fl.nvEncGetEncodePresetConfigEx);
                var pc = new NV_ENC_PRESET_CONFIG();
                pc.version = StructVersionEx(5);
                pc.presetCfg.version = StructVersionEx(9);   // NV_ENC_CONFIG_VER（Sunshine :659）
                pc.presetCfg.encodeCodecConfig = new byte[1792];
                pc.presetCfg.reserved = new uint[278];
                pc.presetCfg.reserved2 = new IntPtr[64];
                pc.presetCfg.rcParams.temporalLayerQP = new byte[8];
                pc.presetCfg.rcParams.viewBitrateRatios = new byte[8];
                IntPtr pcPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NV_ENC_PRESET_CONFIG>());
                var presetGuid = _preset switch { 1 => P1_GUID, 2 => P2_GUID, 4 => P4_GUID, 5 => P5_GUID, 6 => P6_GUID, 7 => P7_GUID, _ => P3_GUID };
int phr;
                try
                {
                    Marshal.StructureToPtr(pc, pcPtr, false);
                    phr = getPreset(_enc, H264_GUID, presetGuid, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, pcPtr);
                    if (phr == 0) pc = (NV_ENC_PRESET_CONFIG)Marshal.PtrToStructure(pcPtr, typeof(NV_ENC_PRESET_CONFIG));
                }
                finally { Marshal.FreeHGlobal(pcPtr); }
                if (phr != 0) { Log("[FAIL] GetEncodePresetConfigEx hr=0x" + phr.ToString("X8")); return 1; }
                Log("[OK  ] GetEncodePresetConfigEx 成功，取到合法预设（rcMode=" + pc.presetCfg.rcParams.rateControlMode + " gop=" + pc.presetCfg.gopLength + " intervalP=" + pc.presetCfg.frameIntervalP + "）");

                var cfg = pc.presetCfg;
                cfg.profileGUID = PROFILE_AUTOSELECT_GUID;
                cfg.rcParams.averageBitRate = (uint)Math.Max(500, _bitrateKbps) * 1000;
                cfg.rcParams.maxBitRate = cfg.rcParams.averageBitRate;
                cfg.rcParams.bitField1 |= RC_ZERO_REORDER_DELAY_BIT;   // zeroReorderDelay=1
                cfg.rcParams.lowDelayKeyFrameScale = 1;
                cfg.rcParams.vbvBufferSize = cfg.rcParams.averageBitRate / 30;
                if (_vbvPct > 0) cfg.rcParams.vbvBufferSize += (uint)((ulong)cfg.rcParams.vbvBufferSize * (uint)_vbvPct / 100u);
                Log("[DEBUG] step2 +zeroReorderDelay+vbv：vbv=" + cfg.rcParams.vbvBufferSize + " bitField1=0x" + cfg.rcParams.bitField1.ToString("X8"));

                // codec union 由 preset 决定（GetEncodePresetConfigEx 返回的 union 未填充，改任何字段都非法）
                IntPtr cfgPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NV_ENC_CONFIG>());
                try { Marshal.StructureToPtr(cfg, cfgPtr, false); init.encodeConfig = cfgPtr; hr = initEnc(_enc, ref init); }
                finally { Marshal.FreeHGlobal(cfgPtr); }
            }
            else
            {
                init.encodeConfig = IntPtr.Zero;
                hr = initEnc(_enc, ref init);
            }
            if (hr != 0) { Log("[FAIL] NvEncInitializeEncoder hr=0x" + hr.ToString("X8")); return 1; }
            Log(_useRcParams ? "[OK  ] NvEncInitializeEncoder 成功（rcParams: CBR=" + _bitrateKbps + "kbps, frameIntervalP=1关B帧, GOP=" + (_gopLength > 0 ? _gopLength.ToString() : "无限") + ", zeroReorderDelay）" : "[OK  ] NvEncInitializeEncoder 成功（P1 + ULL，encodeConfig=NULL）");

            // 输出 bitstream
            var createBs = Marshal.GetDelegateForFunctionPointer<CreateBitstreamBufferFn>(fl.nvEncCreateBitstreamBuffer);
            var cbb = new NV_ENC_CREATE_BITSTREAM_BUFFER();
            cbb.version = StructVersion(1);
            hr = createBs(_enc, ref cbb);
            if (hr != 0) { Log("[FAIL] NvEncCreateBitstreamBuffer hr=0x" + hr.ToString("X8")); return 1; }
            _bitstream = cbb.bitstreamBuffer;
            Log("[OK  ] NvEncCreateBitstreamBuffer 成功");

            // 注册 NV12 纹理为 DIRECTX 输入
            var regRes = Marshal.GetDelegateForFunctionPointer<RegisterResourceFn>(fl.nvEncRegisterResource);
            var rr = new NV_ENC_REGISTER_RESOURCE();
            rr.version = StructVersion(5);
            rr.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX;
            rr.width = (uint)W; rr.height = (uint)H;
            rr.pitch = 0;
            rr.subResourceIndex = 0;
            rr.resourceToRegister = _nv12Tex.NativePointer;
            rr.bufferFormat = NV_ENC_BUFFER_FORMAT_NV12;
            rr.bufferUsage = NV_ENC_INPUT_IMAGE;
            hr = regRes(_enc, ref rr);
            if (hr != 0) { Log("[FAIL] NvEncRegisterResource(DIRECTX,NV12) hr=0x" + hr.ToString("X8")); return 1; }
            _registered = rr.registeredResource;
            Log("[OK  ] NvEncRegisterResource(DIRECTX,NV12) 成功");

            // 编码 N 帧
            var mapRes = Marshal.GetDelegateForFunctionPointer<MapInputResourceFn>(fl.nvEncMapInputResource);
            _unmapResource = Marshal.GetDelegateForFunctionPointer<UnmapInputResourceFn>(fl.nvEncUnmapInputResource);
            var encodePic = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(fl.nvEncEncodePicture);
            var lockBs = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(fl.nvEncLockBitstream);
            var unlockBs = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(fl.nvEncUnlockBitstream);
            _destroyEncoder = Marshal.GetDelegateForFunctionPointer<DestroyEncoderFn>(fl.nvEncDestroyEncoder);
            _unregisterResource = Marshal.GetDelegateForFunctionPointer<UnregisterResourceFn>(fl.nvEncUnregisterResource);
            _destroyBitstream = Marshal.GetDelegateForFunctionPointer<DestroyBitstreamBufferFn>(fl.nvEncDestroyBitstreamBuffer);

            using var outMs = new MemoryStream();
            bool first = true;
            for (int f = 0; f < frames; f++)
            {
                var map = new NV_ENC_MAP_INPUT_RESOURCE();
                map.version = StructVersion(4);
                map.registeredResource = _registered;
                hr = mapRes(_enc, ref map);
                if (hr != 0) { Log("[FAIL] 帧" + f + " MapInputResource hr=0x" + hr.ToString("X8")); return 1; }

                var pic = new NV_ENC_PIC_PARAMS();
                pic.version = StructVersionEx(7);
                pic.inputWidth = (uint)W; pic.inputHeight = (uint)H;
                pic.inputPitch = (uint)W;
                pic.encodePicFlags = first ? (NV_ENC_PIC_FLAG_FORCEIDR | NV_ENC_PIC_FLAG_OUTPUT_SPSPPS) : 0u;
                pic.frameIdx = (uint)f;
                pic.inputTimeStamp = (ulong)f;
                pic.inputDuration = 1000u / 30;
                pic.inputBuffer = map.mappedResource;
                pic.outputBitstream = _bitstream;
                pic.completionEvent = IntPtr.Zero;
                pic.bufferFmt = map.mappedBufferFmt;
                pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;
                hr = encodePic(_enc, ref pic);
                if (hr != 0) { Log("[FAIL] 帧" + f + " EncodePicture hr=0x" + hr.ToString("X8")); _unmapResource(_enc, map.mappedResource); return 1; }

                var lb = new NV_ENC_LOCK_BITSTREAM();
                lb.version = StructVersionEx(2);
                lb.outputBitstream = _bitstream;
                lb.bitField = 0;   // doNotWait=0 阻塞
                hr = lockBs(_enc, ref lb);
                if (hr != 0) { Log("[FAIL] 帧" + f + " LockBitstream hr=0x" + hr.ToString("X8")); _unmapResource(_enc, map.mappedResource); return 1; }

                byte[] nal = new byte[lb.bitstreamSizeInBytes];
                Marshal.Copy(lb.bitstreamBufferPtr, nal, 0, nal.Length);
                outMs.Write(nal, 0, nal.Length);
                Log("[OK  ] 帧" + f + " 出 " + nal.Length + "B  picType=" + PicName(lb.pictureType) + " QP=" + lb.frameAvgQP);

                unlockBs(_enc, _bitstream);
                _unmapResource(_enc, map.mappedResource);
                first = false;
            }

            File.WriteAllBytes(outPath, outMs.ToArray());
            Log("[OK  ] 共 " + frames + " 帧 → " + outPath + "（" + new FileInfo(outPath).Length + "B）");
            Log("[PASS] DIRECTX 直通出帧验证通过");

            TryPresetDemo(fl);
            return 0;
        }

        static void TryPresetDemo(NV_ENCODE_API_FUNCTION_LIST fl)
        {
            try
            {
                var getPreset = Marshal.GetDelegateForFunctionPointer<GetPresetConfigExFn>(fl.nvEncGetEncodePresetConfigEx);
                var pc = new NV_ENC_PRESET_CONFIG();
                pc.version = StructVersionEx(5);
                IntPtr pcPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NV_ENC_PRESET_CONFIG>());
                Marshal.StructureToPtr(pc, pcPtr, false);
                int hr = getPreset(_enc, H264_GUID, P1_GUID, NV_ENC_TUNING_INFO_ULTRA_LOW_LATENCY, pcPtr);
                if (hr != 0) { Log("[WARN] GetEncodePresetConfigEx hr=0x" + hr.ToString("X8") + "（可选）"); Marshal.FreeHGlobal(pcPtr); return; }
                var cfg = (NV_ENC_PRESET_CONFIG)Marshal.PtrToStructure(pcPtr, typeof(NV_ENC_PRESET_CONFIG));
                Log("[OK  ] GetEncodePresetConfigEx(P1,ULL) 成功：rcMode=" + cfg.presetCfg.rcParams.rateControlMode
                    + " _gopLength=" + cfg.presetCfg.gopLength
                    + " frameIntervalP=" + cfg.presetCfg.frameIntervalP
                    + " zeroReorderDelay=" + ((cfg.presetCfg.rcParams.bitField1 >> 9) & 1));
                Marshal.FreeHGlobal(pcPtr);
            }
            catch (Exception ex) { Log("[WARN] 预设演示异常：" + ex.Message); }
        }

        static bool CreateNv12Texture(int w, int h)
        {
            try
            {
                var desc = new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                };
                _nv12Tex = _device.CreateTexture2D(desc);

                int ySize = w * h;
                int total = ySize + ySize / 2;
                var nv12 = new byte[total];
                // 内容图案：横向色带 + 纵向渐变 + 细纹理（拟真内容，避免纯色压缩假数据）
                for (int y = 0; y < h; y++)
                {
                    int band = (y * 12) / h;
                    for (int x = 0; x < w; x++)
                    {
                        int cb = (x * 8) / w;
                        int v = 30 + band * 18 + cb * 12 + ((x % 4 == 0) ? 30 : 0) + ((y % 3 == 0) ? 15 : 0);
                        if (v > 255) v = 255;
                        nv12[y * w + x] = (byte)v;
                    }
                }
                for (int i = 0; i < ySize / 2; i += 2) { nv12[ySize + i] = 128; nv12[ySize + i + 1] = 128; }

                IntPtr p = Marshal.AllocHGlobal(total);
                Marshal.Copy(nv12, 0, p, total);
                _context.UpdateSubresource(_nv12Tex, 0, null, p, (uint)w, (uint)ySize);
                Marshal.FreeHGlobal(p);
                return true;
            }
            catch (Exception ex) { Log("[FAIL] CreateNv12Texture：" + ex.Message); return false; }
        }

        static string PicName(int t) => t switch { 0 => "P", 1 => "B", 2 => "I", 3 => "IDR", _ => "t" + t };

        static void Cleanup()
        {
            try { if (_enc != IntPtr.Zero && _registered != IntPtr.Zero && _unregisterResource != null) _unregisterResource(_enc, _registered); } catch { }
            try { if (_enc != IntPtr.Zero && _bitstream != IntPtr.Zero && _destroyBitstream != null) _destroyBitstream(_enc, _bitstream); } catch { }
            try { if (_enc != IntPtr.Zero && _destroyEncoder != null) _destroyEncoder(_enc); } catch { }
            try { _nv12Tex?.Dispose(); } catch { }
            try { _context?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            try { if (_mod != IntPtr.Zero) FreeLibrary(_mod); } catch { }
        }

        static void Log(string s) => Console.WriteLine(s);
    }
}
