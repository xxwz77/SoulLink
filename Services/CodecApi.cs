using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace SoulLink.Services
{
    /// <summary>
    /// ICodecAPI 手写互操作（Vortice.MediaFoundation 3.8.3 未绑定 ICodecAPI）。
    ///
    /// ★ 依据（2026-11-23 与 FFmpeg mfenc.c / mf_utils.h 对照核实）：
    ///   · IID = 901db4c7-31ce-41a2-85dc-8fa0bf41b8da
    ///   · IUnknown 占槽位 0~2；ICodecAPI::SetValue / GetValue 是接口第 1/2 个方法 → 完整 vtable 槽位 3 / 4。
    ///   · ★ 关键坑（ffmpeg mfenc.c L911）：CODECAPI_AVLowLatencyMode 用 **VT_UI4(1)**，不是 VT_BOOL。
    ///     （VT_BOOL 微软软 MFT 也接受 SetValue=S_OK，但 ffmpeg 权威实现用 VT_UI4，且 MSDN 明确
    ///       「MF 编码器用 VT_BOOL 而非 VT_UI4」的说法对软 MFT 不适用——实测 VT_UI4 是更稳妥的选择。）
    ///   · AVEncCommonRateControlMode 的 CBR = 0（mf_utils.h L129 ff_eAVEncCommonRateControlMode_CBR=0）。
    ///   · 所有 ICodecAPI SetValue 必须在 IMFTransform::SetOutputType 之前（mfenc.c L869-870 注释）。
    ///
    /// ★ SetValue 失败静默降级：不抛异常、不报错，只记录 HRESULT 供诊断。
    /// </summary>
    internal sealed class CodecApi : ComObject
    {
        private const int VT_UI4 = 19;
        private const int VT_BOOL = 11;

        // CODECAPI GUID（与 ffmpeg mf_utils.h 完全一致）
        public static readonly Guid AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
        public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
        public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
        public static readonly Guid AVEncCommonMaxBitRate = new("0f9652a7-8a6c-4a0b-8b0a-a86d3a4c4d00");  // 待核（不用于本轮）
        public static readonly Guid AVEncCommonBufferSize = new("0db96574-b6a4-4c8c-8109-3770d27e11f2");  // 待核（不用于本轮）

        public CodecApi(IntPtr nativePointer) : base(nativePointer) { }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ValueDelegate(IntPtr self, ref Guid name, IntPtr value);

        public int Set(Guid name, IntPtr variant) => Invoke(3, name, variant);
        public int Get(Guid name, IntPtr variant) => Invoke(4, name, variant);

        private int Invoke(int slot, Guid name, IntPtr variant)
        {
            IntPtr vtbl = Marshal.ReadIntPtr(NativePointer);
            var call = Marshal.GetDelegateForFunctionPointer<ValueDelegate>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
            return call(NativePointer, ref name, variant);
        }

        /// <summary>造一个 VT_UI4 VARIANT（16 字节：8 头部 + 8 标量）。返回的指针需调用方释放。</summary>
        public static IntPtr VariantUi4(uint value)
        {
            IntPtr p = Marshal.AllocHGlobal(16);
            Marshal.Copy(new byte[16], 0, p, 16);
            Marshal.WriteInt16(p, (short)VT_UI4);
            Marshal.WriteInt32(p, IntPtr.Size, unchecked((int)value));
            return p;
        }

        /// <summary>SetValue 静默降级：失败只返回 HRESULT，不抛、不报错。</summary>
        public static int TrySet(CodecApi? api, Guid key, uint value, string label, Action<string>? log = null)
        {
            if (api == null || api.NativePointer == IntPtr.Zero) return -1;
            IntPtr variant = VariantUi4(value);
            try
            {
                int hr = api.Set(key, variant);
                if (hr < 0 && log != null) log("[codecapi] " + label + "=0x" + hr.ToString("X8") + "（静默降级，不阻塞）");
                return hr;
            }
            finally { Marshal.FreeHGlobal(variant); }
        }
    }
}
