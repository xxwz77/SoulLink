// ============================================================================
// Window —— 最小 GDI 窗口：NV12 → RGB32 → SetDIBitsToDevice 上屏。
// 选 GDI 而非 D3D：探针目标是「给浏览器一条非浏览器参照」，GDI 路径最短
// （无 GPU 上下文管理），上屏计时即 BitBlt 完成；代价是 CPU 转换 ~2-4ms@1080p，
// 该成本计入 t2 且在报告中单列（不与服务端/网络混淆）。
// ============================================================================
using System;
using System.Runtime.InteropServices;

namespace DirectProbe;

sealed class Window : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptx, pty; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int L, T, R, B; }

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll")] static extern short RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32.dll")] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG msg, IntPtr h, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetDIBitsToDevice(IntPtr hdc, int xDst, int yDst, int w, int h, int xSrc, int ySrc, int start, int lines, byte[] bits, ref BITMAPINFO bmi, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSW { public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName; [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public uint biSize; public int biWidth; public int biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColorsR, bmiColorsG, bmiColorsB; }

    const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    const int CW_USEDEFAULT = unchecked((int)0x80000000);

    readonly IntPtr _hwnd;
    readonly WndProc _proc;          // 防 GC 回收委托
    readonly IntPtr _hdc;
    byte[]? _rgb;
    bool _closed;

    static WndProc? _staticProc;     // 类级：实例方法包装
    static Window? _self;

    public bool ClosedByUser => _closed;

    public Window(string title, int w, int h)
    {
        _proc = WndProcImpl;
        var wc = new WNDCLASSW
        {
            lpfnWndProc = _proc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = "DirectProbeWnd",
            hbrBackground = IntPtr.Zero,
        };
        RegisterClassW(ref wc);
        _hwnd = CreateWindowExW(0, "DirectProbeWnd", title, 0x00CF0000 /*WS_OVERLAPPEDWINDOW*/,
            CW_USEDEFAULT, CW_USEDEFAULT, w, h, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        ShowWindow(_hwnd, 5 /*SW_SHOW*/);
        UpdateWindow(_hwnd);
        _hdc = GetDC(_hwnd);
        _self = this;
    }

    static IntPtr WndProcImpl(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_CLOSE || msg == WM_DESTROY) { if (_self != null) _self._closed = true; return IntPtr.Zero; }
        return DefWindowProcW(h, msg, w, l);
    }

    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>NV12 → RGB32 → SetDIBitsToDevice（含窗口消息泵处理）。</summary>
    public void Blit(byte[] nv12, int w, int h, int stride)
    {
        Pump();
        if (_closed) return;
        int plane = stride * h;
        if (_rgb == null || _rgb.Length < w * h * 4) _rgb = new byte[w * h * 4];
        // NV12: Y 平面 stride*h，UV 平面 stride*h/2，2x2 采样
        int uvOffset = plane;
        for (int y = 0; y < h; y++)
        {
            int yRow = y * stride;
            int uvRow = uvOffset + (y >> 1) * stride;
            int dst = (h - 1 - y) * w * 4;   // bottom-up DIB
            for (int x = 0; x < w; x++)
            {
                int Y = nv12[yRow + x];
                int U = nv12[uvRow + (x & ~1)] - 128;
                int V = nv12[uvRow + (x & ~1) + 1] - 128;
                _rgb[dst + 0] = (byte)Math.Clamp(Y + ((91881 * V) >> 16), 0, 255);
                _rgb[dst + 1] = (byte)Math.Clamp(Y - (((22554 * U) + (46802 * V)) >> 16), 0, 255);
                _rgb[dst + 2] = (byte)Math.Clamp(Y + ((116130 * U) >> 16), 0, 255);
                _rgb[dst + 3] = 255;
                dst += 4;
            }
        }
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = h;   // 正高度 = bottom-up，与上面翻转一致
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        SetDIBitsToDevice(_hdc, 0, 0, w, h, 0, 0, 0, h, _rgb, ref bmi, 0);
    }

    void Pump()
    {
        while (PeekMessageW(out MSG m, _hwnd, 0, 0, 1 /*PM_REMOVE*/)) { }
    }

    public void Dispose()
    {
        try { ReleaseDC(_hwnd, _hdc); } catch { }
        try { DestroyWindow(_hwnd); } catch { }
        _self = null;
    }
}
