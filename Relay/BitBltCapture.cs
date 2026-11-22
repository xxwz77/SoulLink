using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SoulLink.Relay
{
    /// <summary>
    /// BitBlt 抓屏后端（第一步：行为与旧 SessionRecorder.GrabJpeg 完全一致）。
    ///
    /// ★ 搬迁说明：
    ///   把原 SessionRecorder 里的「Win32 P/Invoke + 光标叠加 + JPEG 编码」原样搬到这里，
    ///   一个字节逻辑都不改。SessionRecorder 只负责环形缓冲 / 录制 / 直播调度，抓帧细节
    ///   交给 IScreenCapture 实现。第二步 WGC 实现同一接口即可无缝替换。
    /// </summary>
    internal sealed class BitBltCapture : IScreenCapture
    {
        public string Name => "BitBlt";

        // ── Win32 ────────────────────────────────────────────────────────────
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;

        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y,
            IntPtr hIcon, int cx, int cy, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h,
            IntPtr hdcSrc, int x1, int y1, uint rop);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

        private const uint SRCCOPY = 0x00CC0020;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private ImageCodecInfo? _jpeg;

        private ImageCodecInfo JpegCodec()
        {
            if (_jpeg != null) return _jpeg;
            foreach (var c in ImageCodecInfo.GetImageEncoders())
                if (c.FormatID == ImageFormat.Jpeg.Guid) { _jpeg = c; break; }
            return _jpeg ?? throw new InvalidOperationException("找不到 JPEG 编码器");
        }

        /// <summary>
        /// 把当前鼠标光标叠加到已抓取的屏幕位图上。
        /// （原样搬自 SessionRecorder.DrawCursorOn，注释保留以说明三个坑。）
        /// </summary>
        private static void DrawCursorOn(IntPtr hdc, int w, int h)
        {
            var ci = new CURSORINFO();
            ci.cbSize = Marshal.SizeOf<CURSORINFO>();
            if (!GetCursorInfo(ref ci)) return;
            if ((ci.flags & CURSOR_SHOWING) == 0) return;   // 光标隐藏时不画
            if (ci.hCursor == IntPtr.Zero) return;

            int x = ci.ptScreenPos.X;
            int y = ci.ptScreenPos.Y;

            // 用热点修正实际绘制的左上角
            if (GetIconInfo(ci.hCursor, out ICONINFO info))
            {
                try
                {
                    x -= info.xHotspot;
                    y -= info.yHotspot;
                }
                finally
                {
                    if (info.hbmMask != IntPtr.Zero) { try { DeleteObject(info.hbmMask); } catch { } }
                    if (info.hbmColor != IntPtr.Zero) { try { DeleteObject(info.hbmColor); } catch { } }
                }
            }

            // 完全在画面外就不画（省一次绘制）
            if (x < -64 || y < -64 || x > w + 64 || y > h + 64) return;

            // 0,0 表示用图标原始尺寸
            DrawIconEx(hdc, x, y, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }

        public byte[]? CaptureJpeg(int quality, out int width, out int height)
        {
            width = 0;
            height = 0;
            IntPtr hScreen = IntPtr.Zero, hMem = IntPtr.Zero, hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                int w = GetSystemMetrics(SM_CXSCREEN);
                int h = GetSystemMetrics(SM_CYSCREEN);
                if (w <= 0 || h <= 0) return null;
                width = w; height = h;

                hScreen = GetDC(IntPtr.Zero);
                if (hScreen == IntPtr.Zero) return null;
                hMem = CreateCompatibleDC(hScreen);
                hBmp = CreateCompatibleBitmap(hScreen, w, h);
                if (hMem == IntPtr.Zero || hBmp == IntPtr.Zero) return null;
                old = SelectObject(hMem, hBmp);
                if (!BitBlt(hMem, 0, 0, w, h, hScreen, 0, 0, SRCCOPY)) return null;

                try { DrawCursorOn(hMem, w, h); } catch { }

                using var bmp = Image.FromHbitmap(hBmp);
                using var ms = new MemoryStream();
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                bmp.Save(ms, JpegCodec(), ep);
                return ms.ToArray();
            }
            catch { return null; }
            finally
            {
                if (hMem != IntPtr.Zero && old != IntPtr.Zero) { try { SelectObject(hMem, old); } catch { } }
                if (hBmp != IntPtr.Zero) { try { DeleteObject(hBmp); } catch { } }
                if (hMem != IntPtr.Zero) { try { DeleteDC(hMem); } catch { } }
                if (hScreen != IntPtr.Zero) { try { ReleaseDC(IntPtr.Zero, hScreen); } catch { } }
            }
        }
    }
}
