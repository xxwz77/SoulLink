using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class PebSweep
{
    const uint PQ = 0x1000, VM = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    struct PBI { public IntPtr R1, Peb, R2, R3, Pid, R4; }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr p, IntPtr b, byte[] buf, int size, out IntPtr read);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr p, int c, out PBI i, int s, out int r);

    static IntPtr ReadPtr(IntPtr p, IntPtr addr)
    {
        byte[] b = new byte[8];
        return ReadProcessMemory(p, addr, b, 8, out _) ? new IntPtr(BitConverter.ToInt64(b, 0)) : IntPtr.Zero;
    }

    public static int Main()
    {
        int pid = Process.GetCurrentProcess().Id;
        Console.WriteLine("self pid = " + pid + "  Is64 = " + Environment.Is64BitProcess);

        IntPtr h = OpenProcess(PQ | VM, false, pid);
        if (h == IntPtr.Zero) { Console.WriteLine("OpenProcess failed " + Marshal.GetLastWin32Error()); return 1; }
        try
        {
            if (NtQueryInformationProcess(h, 0, out PBI pbi, Marshal.SizeOf<PBI>(), out _) != 0)
            { Console.WriteLine("NtQueryInformationProcess failed"); return 1; }

            Console.WriteLine("PEB = 0x" + pbi.Peb.ToInt64().ToString("X"));
            IntPtr prm = ReadPtr(h, pbi.Peb + 0x20);
            Console.WriteLine("ProcessParameters (PEB+0x20) = 0x" + prm.ToInt64().ToString("X"));

            // 扫描 RTL_USER_PROCESS_PARAMETERS 前 0x100 字节，找疑似 UNICODE_STRING
            // （Buffer 指针可读 + Length 合理 + 内容是合法 UTF-16 路径）
            for (int off = 0x00; off < 0x100; off += 8)
            {
                IntPtr buf = ReadPtr(h, prm + off);
                if (buf == IntPtr.Zero) continue;
                byte[] lenb = new byte[4];
                if (!ReadProcessMemory(h, prm + off + 8, lenb, 4, out _)) continue;
                int len = BitConverter.ToUInt16(lenb, 0);
                int maxLen = BitConverter.ToUInt16(lenb, 2);
                if (len <= 0 || len > 0x2000 || maxLen < len) continue;

                byte[] raw = new byte[len];
                if (!ReadProcessMemory(h, buf, raw, len, out IntPtr got)) continue;
                string s = Encoding.Unicode.GetString(raw);
                if (s.Length < 3 || s.Any(c => c < 32 && c != 0)) continue;
                Console.WriteLine("  偏移 0x" + off.ToString("X2") + "  len=" + len + "  => " + s);
            }
        }
        finally { CloseHandle(h); }
        return 0;
    }
}
