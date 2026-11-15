using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>光标相关的原生调用（捕获模式要用）。</summary>
    public static class NativeInput
    {
        [DllImport("user32.dll", EntryPoint = "SetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPosNative(int x, int y);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPosNative(out POINT p);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        public static bool SetCursorPos(int x, int y)
        {
            try { return SetCursorPosNative(x, y); } catch { return false; }
        }

        public static (int x, int y) GetCursorPos()
        {
            try
            {
                if (GetCursorPosNative(out POINT p)) return (p.X, p.Y);
            }
            catch { }
            return (-1, -1);
        }
    }
}
