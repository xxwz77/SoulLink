using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamVerify
{
    /// <summary>
    /// 「假宿主」：建 relay 管道、起独立 agent、下发指令、收日志。
    ///
    /// ★ 为什么验证 /stream 必须由本工具代持宿主：
    ///   真实 Agent 的画面来自 SessionRecorder 环形缓冲，而缓冲只有在
    ///   **宿主下发 record 指令**之后才开始抓帧。浏览器 / curl 没法下发 record，
    ///   所以不代持宿主时打 /stream 只会得到 404「缓冲里没有帧」——
    ///   会被误判成「功能坏了」，其实只是没开始抓帧。这个误判历史上发生过。
    ///
    /// 安全边界：
    ///   · 只启动/结束**自己 Process.Start 出来的那个 PID**，绝不按名字或路径通配；
    ///   · 只监听自己算出来的端口，只写 %TEMP% 下的目录。
    /// </summary>
    public sealed class ProbeHost : IAsyncDisposable
    {
        private NamedPipeServerStream? _pipe;
        private NamedPipeServerStream? _events;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private CancellationTokenSource? _rxCts;
        private Task? _rx;
        private readonly StringBuilder _inbox = new();
        private Process? _agent;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public string? LogPath { get; private set; }
        public int AgentPid { get; private set; }

        public bool AgentAlive
        {
            get { try { return _agent != null && !_agent.HasExited; } catch { return false; } }
        }

        public int AgentExitCode
        {
            get { try { return _agent is { HasExited: true } ? _agent.ExitCode : -1; } catch { return -1; } }
        }

        public static async Task<ProbeHost?> StartAsync(string agentDir, int port, string token,
            string captureDir, Action<string>? log = null, string captureMode = "1")
        {
            void L(string s) => log?.Invoke("   " + s);

            var h = new ProbeHost();
            string exe = Path.Combine(agentDir, "SoulLink.Agent.exe");
            if (!File.Exists(exe)) { log?.Invoke("找不到 " + exe); return null; }

            int sid = Process.GetCurrentProcess().SessionId;
            string name = "SoulLink_Relay_" + sid + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            L("宿主管道 " + name);

            h._pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                h._events = new NamedPipeServerStream(name + "_Events", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch { }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = agentDir
            };
            psi.ArgumentList.Add("--agent");
            psi.ArgumentList.Add("--pipe");
            psi.ArgumentList.Add(name);
            psi.Environment["SOULLINK_WEB_PORT"] = port.ToString();
            psi.Environment["SOULLINK_WEB_TOKEN"] = token;
            // ★ 抓屏后端必须可配：之前这里硬编码 "1"（WGC），
            //   导致设 SOULLINK_CAPTURE_MODE=0 做 BitBlt 对照实验时实际跑的还是 WGC，
            //   得出的结论是错的（"设了 0 仍报 WGC" → 其实是本工具没传）。
            psi.Environment["SOULLINK_CAPTURE_MODE"] = captureMode;
            psi.Environment["SOULLINK_ACCOUNT_ID"] = "verify";
            psi.Environment["SOULLINK_CAPTURE_DIR"] = captureDir;

            h._agent = Process.Start(psi);
            if (h._agent == null) { log?.Invoke("代理启动失败"); return null; }
            h.AgentPid = h._agent.Id;
            L("代理 PID " + h._agent.Id);
            h.LogPath = Path.Combine(Path.GetTempPath(), "soullink-relay-" + h._agent.Id + ".log");

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try { await h._pipe.WaitForConnectionAsync(cts.Token); }
                catch (OperationCanceledException)
                {
                    log?.Invoke("代理 30 秒内没有连上管道");
                    await h.StopAsync();
                    return null;
                }
            }

            h._reader = new StreamReader(h._pipe, new UTF8Encoding(false), false, 4096, true);
            h._writer = new StreamWriter(h._pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            h._rxCts = new CancellationTokenSource();
            h._rx = Task.Run(() => h.RxLoopAsync(h._rxCts.Token));

            var up = Stopwatch.StartNew();
            bool listening = false;
            while (up.Elapsed.TotalSeconds < 15)
            {
                try
                {
                    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    probe.DefaultRequestHeaders.Add("X-SoulLink-Token", token);
                    var r = await probe.GetAsync($"http://127.0.0.1:{port}/api/info");
                    if ((int)r.StatusCode is 200 or 401) { listening = true; break; }
                }
                catch { }
                await Task.Delay(400);
            }
            if (!listening)
            {
                log?.Invoke("代理 15 秒内没有在 :" + port + " 上监听");
                await h.StopAsync();
                return null;
            }
            L("网页服务在 :" + port + " 就绪");
            return h;
        }

        private async Task RxLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    string? line = await _reader!.ReadLineAsync(ct);
                    if (line == null) break;
                    lock (_inbox) _inbox.AppendLine(line);
                }
            }
            catch { }
        }

        /// <summary>发一条指令并等它的回复（协议是串行请求-响应）。</summary>
        public async Task<string?> SendAsync(string json)
        {
            await _lock.WaitAsync();
            try
            {
                int before;
                lock (_inbox) before = _inbox.ToString().Length;
                if (_writer == null) return null;
                await _writer.WriteLineAsync(json);
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 8)
                {
                    string snap;
                    lock (_inbox) snap = _inbox.ToString();
                    if (snap.Length > before)
                    {
                        string added = snap.Substring(before);
                        int nl = added.IndexOf('\n');
                        if (nl >= 0) return added.Substring(0, nl).Trim();
                    }
                    await Task.Delay(60);
                }
                return null;
            }
            finally { _lock.Release(); }
        }

        public List<string> FullLog() => TailLog(int.MaxValue);

        public List<string> TailLog(int n)
        {
            try
            {
                if (LogPath != null && File.Exists(LogPath))
                {
                    var lines = File.ReadAllLines(LogPath);
                    return lines.Skip(Math.Max(0, lines.Length - n)).ToList();
                }
            }
            catch { }
            return new List<string>();
        }

        public async Task StopAsync()
        {
            try { _rxCts?.Cancel(); } catch { }
            try { if (_writer != null) await _writer.WriteLineAsync("{\"Cmd\":\"exit\"}"); } catch { }
            await Task.Delay(400);
            try { _pipe?.Dispose(); } catch { }
            try { _events?.Dispose(); } catch { }
            // ★ 只结束自己启动的那个 PID，绝不按名字/路径通配
            try
            {
                if (_agent != null && !_agent.HasExited) _agent.Kill(true);
                _agent?.WaitForExit(5000);
            }
            catch { }
            try { _agent?.Dispose(); } catch { }
        }

        public async ValueTask DisposeAsync() => await StopAsync();
    }
}
