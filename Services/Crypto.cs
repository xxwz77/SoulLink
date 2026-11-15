using System;
using System.Runtime.InteropServices;
using System.Text;
using SoulLink.Services;

namespace SoulLink.Core
{
    /// <summary>使用 Windows DPAPI（当前用户）加密保存密码，避免明文落盘。</summary>
    public static class SecretBox
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var data = Encoding.UTF8.GetBytes(plain);
            var inBlob = new Native.DATA_BLOB();
            var outBlob = new Native.DATA_BLOB();
            IntPtr pIn = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, pIn, data.Length);
                inBlob.cbData = data.Length;
                inBlob.pbData = pIn;
                if (!Native.CryptProtectData(ref inBlob, "SoulLink", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    throw new InvalidOperationException("DPAPI 加密失败: " + Marshal.GetLastWin32Error());
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, outBlob.cbData);
                return "dpapi:" + Convert.ToBase64String(buf);
            }
            finally
            {
                Marshal.FreeHGlobal(pIn);
                if (outBlob.pbData != IntPtr.Zero) Native.LocalFree(outBlob.pbData);
            }
        }

        /// <summary>解密；失败返回空字符串（旧版明文会原样返回）。</summary>
        public static string Unprotect(string? cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return "";
            if (!cipher.StartsWith("dpapi:", StringComparison.Ordinal))
                return cipher; // 兼容手工填写的明文

            byte[] data;
            try { data = Convert.FromBase64String(cipher.Substring(6)); }
            catch { return ""; }

            var inBlob = new Native.DATA_BLOB();
            var outBlob = new Native.DATA_BLOB();
            IntPtr pIn = Marshal.AllocHGlobal(data.Length);
            try
            {
                Marshal.Copy(data, 0, pIn, data.Length);
                inBlob.cbData = data.Length;
                inBlob.pbData = pIn;
                if (!Native.CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                    return "";
                var buf = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, buf, 0, outBlob.cbData);
                return Encoding.UTF8.GetString(buf);
            }
            catch { return ""; }
            finally
            {
                Marshal.FreeHGlobal(pIn);
                if (outBlob.pbData != IntPtr.Zero) Native.LocalFree(outBlob.pbData);
            }
        }
    }
}
