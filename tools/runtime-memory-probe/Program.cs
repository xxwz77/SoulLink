using System.Diagnostics.Tracing;
using Microsoft.Diagnostics.NETCore.Client;

int pid = int.Parse(args[0]);
var client = new DiagnosticsClient(pid);
var providers = new[] { new EventPipeProvider("System.Runtime", EventLevel.Informational, 0,
    new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }) };
using var session = client.StartEventPipeSession(providers, false);
var task = Task.Run(() => {
    using var reader = new StreamReader(session.EventStream);
    while (!reader.EndOfStream)
    {
        string line = reader.ReadLine();
        if (line.Contains("GC Heap Size") || line.Contains("Allocation Rate") || line.Contains("Number of Active Timers") ||
            line.Contains("ThreadPool Thread Count") || line.Contains("CPU Usage") || line.Contains("Working Set"))
            Console.WriteLine(line);
    }
});
await Task.Delay(6500);
session.Stop();
await task;
