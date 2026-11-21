using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>与 1Remote 使用的 IMsTscNonScriptable 对应的 COM 接口。</summary>
    [ComImport]
    [Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMsTscNonScriptable
    {
        [DispId(1)]
        string ClearTextPassword { set; }
    }

    /// <summary>
    /// mstscax 的命名属性通道（Win8+，IUnknown 直系）。
    /// vtable 顺序已用 typelib 探针核实（2026-11-21，System32/SysWOW64 一致）：
    /// put_Property 在 0x18，get_Property 在 0x20——只声明 put 也是正确槽位。
    /// </summary>
    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMsRdpExtendedSettings
    {
        /// <summary>put_Property。value 按 VARIANT 编组（bool → VT_BOOL）。</summary>
        void SetProperty([MarshalAs(UnmanagedType.BStr)] string propertyName, ref object value);
    }
}