using System;
using System.Diagnostics;
using SoulLink.Services;

public static class CmdLineProbe
{
    public static int Main()
    {
        Console.WriteLine("self pid=" + Environment.ProcessId + " Is64=" + Environment.Is64BitProcess);
        string cmd = ProcessCommandLine.Read(Environment.ProcessId);
        Console.WriteLine("cmd  = [" + cmd + "]");
        Console.WriteLine("err  = " + (string.IsNullOrEmpty(ProcessCommandLine.LastError) ? "(none)" : ProcessCommandLine.LastError));
        return cmd.Length > 0 ? 0 : 1;
    }
}
