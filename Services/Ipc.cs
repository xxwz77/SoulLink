using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SoulLink.Models;

namespace SoulLink.Services
{
    /// <summary>
    /// 单实例 + 命令通道：用命名管道。第二次运行程序时，新进程把命令行参数发给
    /// 已经在运行的实例，然后静默退出 —— 用户只会看到一个开机常驻的托盘图标。
    /// </summary>
    public static class Ipc
    {
        private const string PipeName = "SoulLink.IPC.v1";
        private const string MutexName = "Global\\SoulLink.SingleInstance.v1";

        private static readonly JsonSerializerOptions Opt = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>仅当当前进程真正持有互斥体时才非空。</summary>
        public static Mutex? Owned { get; private set; }

        /// <summary>true = 当前进程是唯一实例</summary>
        public static bool TryAcquire()
        {
            if (Owned != null) return true;
            Mutex? m = null;
            try
            {
                m = new Mutex(true, MutexName, out bool created);
                if (created)
                {
                    Owned = m;
                    return true;
                }
            }
            catch
            {
                // 权限异常时按“唯一实例”处理，至少不要挡住用户
                Owned = m;
                return true;
            }
            try { m?.Dispose(); } catch { }
            return false;
        }

        /// <summary>把请求发给已运行的实例。</summary>
        public static IpcResponse? Send(InvokeRequest request, int timeoutMs = 120000)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(5000);

                    // 注意：reader / writer 不要再套 using —— 先关掉其中一个会把管道关掉，
                    // 后面的 Dispose 就会抛 “Cannot access a closed pipe”。统一交给 pipe 释放。
                    var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    var reader = new StreamReader(pipe, Encoding.UTF8);

                    writer.WriteLine(JsonSerializer.Serialize(request, Opt));
                    writer.Flush();

                    string? line = reader.ReadLine();
                    if (line == null)
                    {
                        IpcLog("client: empty response");
                        return null;
                    }
                    return JsonSerializer.Deserialize<IpcResponse>(line, Opt);
                }
            }
            catch (Exception ex)
            {
                IpcLog("client error: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private static readonly object LogLock = new object();

        public static void IpcLog(string text)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.ConfigDir);
                lock (LogLock)
                    File.AppendAllText(Path.Combine(ConfigStore.ConfigDir, "ipc.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  [pid " + Environment.ProcessId + "]  " + text + Environment.NewLine,
                        new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>服务端：收到请求后交给 handler 处理。</summary>
        public static async Task ListenAsync(Func<InvokeRequest, Task<IpcResponse>> handler, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (server)
                            using (var reader = new StreamReader(server, Encoding.UTF8))
                            using (var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true })
                            {
                                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                                if (string.IsNullOrWhiteSpace(line)) { IpcLog("server: empty request"); return; }
                                IpcLog("server: request " + (line!.Length > 200 ? line.Substring(0, 200) : line));
                                var req = JsonSerializer.Deserialize<InvokeRequest>(line!, Opt) ?? new InvokeRequest();
                                var resp = await handler(req).ConfigureAwait(false);
                                IpcLog("server: response ok=" + resp.Ok + " msg=" + resp.Message);
                                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, Opt)).ConfigureAwait(false);
                                await writer.FlushAsync().ConfigureAwait(false);
                                // 等客户端把数据读走再关闭管道，否则对方会拿到 “closed pipe”
                                try { server.WaitForPipeDrain(); } catch { }
                                await Task.Delay(60).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            IpcLog("server handler error: " + ex.GetType().Name + " " + ex.Message);
                        }
                    }, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    IpcLog("listen error: " + ex.GetType().Name + " " + ex.Message);
                    try { server?.Dispose(); } catch { }
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }
    }
}