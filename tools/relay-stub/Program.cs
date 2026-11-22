using System.IO.Pipes;
using System.Text;

static string Option(string[] args, string key, string fallback)
{
    int i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

string pipeName = Option(args, "--pipe", "SoulLink.Verify");
int duration = int.TryParse(Option(args, "--duration", "30"), out int n) ? Math.Clamp(n, 1, 1800) : 30;
string logPath = Path.Combine(Path.GetTempPath(), "soullink-relay-stub-" + Environment.ProcessId + ".log");
void Log(string message) => File.AppendAllText(logPath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(duration));
using var command = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
using var events = new NamedPipeServerStream(pipeName + "_Events", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
Log("START pipe=" + pipeName + " duration=" + duration);
try
{
    Task commands = command.WaitForConnectionAsync(cts.Token);
    Task eventConnection = events.WaitForConnectionAsync(cts.Token);
    await commands;
    await eventConnection;
    Log("PIPES_CONNECTED");
    if (args.Contains("--record-on-connect"))
    {
        // 只启动代理自己的只读抓屏；本桩不执行输入，也不接收输入请求。
        int fps = int.TryParse(Option(args, "--fps", "15"), out int requested) ? Math.Clamp(requested, 1, 30) : 15;
        byte[] start = Encoding.UTF8.GetBytes("{\"Cmd\":\"record\",\"Count\":" + fps + ",\"X\":75,\"Delta\":5}\n");
        await command.WriteAsync(start, cts.Token);
        await command.FlushAsync(cts.Token);
        Log("RECORD_REQUESTED");
    }
    int saveAfter = int.TryParse(Option(args, "--save-after", "0"), out int delay) ? delay : 0;
    if (saveAfter > 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(saveAfter), cts.Token);
        byte[] save = Encoding.UTF8.GetBytes("{\"Cmd\":\"save\",\"Button\":\"verify\",\"Hotkey\":\"nal\",\"X\":5,\"Delta\":0}\n");
        await command.WriteAsync(save, cts.Token);
        await command.FlushAsync(cts.Token);
        Log("SAVE_REQUESTED");
    }
    // 只读采集桩：不反序列化、不执行任何输入指令，只对协议请求给成功回复。
    await foreach (string? line in ReadLinesAsync(command, cts.Token))
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            byte[] reply = Encoding.UTF8.GetBytes("{\"Ok\":true,\"Message\":\"只读采集宿主桩\"}\n");
            await command.WriteAsync(reply, cts.Token);
            await command.FlushAsync(cts.Token);
        }
    }
}
catch (OperationCanceledException) { Log("TIMEOUT"); }
catch (IOException ex) { Log("DISCONNECTED " + ex.Message); }
finally { Log("STOP"); }
return 0;

static async IAsyncEnumerable<string?> ReadLinesAsync(Stream stream,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
{
    var line = new MemoryStream();
    var buffer = new byte[4096];
    while (!ct.IsCancellationRequested)
    {
        int count = await stream.ReadAsync(buffer, ct);
        if (count == 0) yield break;
        for (int i = 0; i < count; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                yield return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.SetLength(0);
            }
            else line.WriteByte(buffer[i]);
        }
    }
}
