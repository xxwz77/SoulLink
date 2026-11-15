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
}