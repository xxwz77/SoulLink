using System;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

/// <summary>
/// DXGI Desktop Duplication 在 RDP 会话内可行性探针。
/// 对照 Sunshine display_base.cpp / misc.cpp 的前提：
///   1. syncThreadDesktop（OpenInputDesktop + SetThreadDesktop）
///   2. D3D11CreateDevice 用 D3D_DRIVER_TYPE_UNKNOWN（不是 Hardware）
///   3. Optimus hook（本探针不含，纯 N 卡混合本才需要）
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll")] static extern bool SetThreadDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr hDesktop);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    const uint DF_ALLOWOTHERACCOUNTHOOK = 0x0001;
    const uint GENERIC_ALL = 0x10000000;
    const int SM_REMOTESESSION = 0x1000;

    public static int Main()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        Console.WriteLine("=== DXGI Desktop Duplication RDP 可行性探针 ===");
        Console.WriteLine("SessionId=" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
        Console.WriteLine("SM_REMOTESESSION=" + (GetSystemMetrics(SM_REMOTESESSION) != 0 ? "1 (RDP)" : "0 (本地控制台)"));

        int exit = 0;

        // 前提 1：syncThreadDesktop
        Console.WriteLine("--- 前提1: syncThreadDesktop ---");
        IntPtr hDesk = OpenInputDesktop(DF_ALLOWOTHERACCOUNTHOOK, false, GENERIC_ALL);
        if (hDesk == IntPtr.Zero)
        {
            Console.WriteLine("OpenInputDesktop 失败 err=" + Marshal.GetLastWin32Error() + "（继续）");
        }
        else
        {
            Console.WriteLine("OpenInputDesktop OK");
            if (!SetThreadDesktop(hDesk)) Console.WriteLine("SetThreadDesktop 失败 err=" + Marshal.GetLastWin32Error());
            else Console.WriteLine("SetThreadDesktop OK");
            CloseDesktop(hDesk);
        }

        // 前提 2：D3D11 设备用 DriverType.Unknown（对照 Hardware）
        Console.WriteLine("--- 前提2: D3D11CreateDevice ---");
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
            FeatureLevel.Level_9_3, FeatureLevel.Level_9_2, FeatureLevel.Level_9_1
        };

        var hrUnknown = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Unknown, DeviceCreationFlags.BgraSupport, featureLevels,
            out var devUnknown, out var featUnknown, out var ctxUnknown);
        Console.WriteLine("DriverType.Unknown hr=0x" + hrUnknown.Code.ToString("X8")
            + (hrUnknown.Success ? " OK FL=" + featUnknown : " FAIL"));

        var hrHardware = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels,
            out var devHardware, out var featHardware, out var ctxHardware);
        Console.WriteLine("DriverType.Hardware hr=0x" + hrHardware.Code.ToString("X8")
            + (hrHardware.Success ? " OK FL=" + featHardware : " FAIL"));

        ID3D11Device device = hrUnknown.Success ? devUnknown : (hrHardware.Success ? devHardware : null);
        if (device == null)
        {
            Console.WriteLine("FAIL: 硬件 D3D11 设备都建不出（Unknown/Hardware 都失败）");
            return 2;
        }

        // 前提 3：枚举输出 + DuplicateOutput
        Console.WriteLine("--- 前提3: 枚举输出 + DuplicateOutput ---");
        using (device)
        {
            using IDXGIDevice dxgiDev = device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDev.GetAdapter();

            for (int oi = 0; ; oi++)
            {
                Result ohr = adapter.EnumOutputs((uint)oi, out IDXGIOutput output);
                if (!ohr.Success || output == null) break;
                using (output)
                {
                    string devName = "?";
                    int w = 0, h = 0;
                    try
                    {
                        var od = output.Description;
                        devName = od.DeviceName.ToString().TrimEnd('\0');
                        w = od.DesktopCoordinates.Right - od.DesktopCoordinates.Left;
                        h = od.DesktopCoordinates.Bottom - od.DesktopCoordinates.Top;
                    }
                    catch { }
                    Console.WriteLine("  output[" + oi + "] " + devName + " " + w + "x" + h);

                    try
                    {
                        using IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                        try
                        {
                            IDXGIOutputDuplication dup = output1.DuplicateOutput(device);
                            using (dup)
                            {
                                var dd = dup.Description;
                                Console.WriteLine("    DuplicateOutput 成功！hr=OK  桌面 " + dd.ModeDescription.Width + "x" + dd.ModeDescription.Height
                                    + " fmt=" + dd.ModeDescription.Format);
                                TryAcquire(dup, 3000);
                            }
                        }
                        catch (SharpGenException ex)
                        {
                            int code = ex.ResultCode.Code;
                            Console.WriteLine("    DuplicateOutput 失败 hr=0x" + code.ToString("X8"));
                            if (code != 0) exit = 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("    QueryInterface<IDXGIOutput1> 失败: " + ex.Message);
                    }
                }
            }
        }

        return exit;
    }

    static void TryAcquire(IDXGIOutputDuplication dup, int timeoutMs)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tries = 0;
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                tries++;
                Result hr = dup.AcquireNextFrame(100, out _, out IDXGIResource resource);
                if (hr.Success && resource != null)
                {
                    Console.WriteLine("    ✓ AcquireNextFrame 成功（第 " + tries + " 次，"
                        + sw.ElapsedMilliseconds + "ms）→ 能出帧！");
                    resource.Dispose();
                    dup.ReleaseFrame();
                    return;
                }
                else if (hr.Code == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT
                {
                    continue;
                }
                else
                {
                    Console.WriteLine("    AcquireNextFrame 失败 hr=0x" + hr.Code.ToString("X8"));
                    return;
                }
            }
            Console.WriteLine("    AcquireNextFrame 超时（" + timeoutMs + "ms，画面可能静止）");
        }
        catch (Exception ex)
        {
            Console.WriteLine("    AcquireNextFrame 异常: " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}
