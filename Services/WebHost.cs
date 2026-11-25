using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>
    /// 网页入口宿主：**启动/停止独立服务 <c>SoulLink.TaskEdit.exe</c>**，并为主程序
    /// 自己的回环 WebServer 分配内部端口（设计文档 §11.4 / §11.6 / §17.1）。
    ///
    /// ── 分工 ─────────────────────────────────────────────────────────
    ///   主程序（本类）          独立服务（SoulLink.TaskEdit）
    ///   · 决定对外端口 WebPort ← 参数传入，独占监听
    ///   · 决定内部回环端口     → 参数传入，反代目标
    ///   · 起/停子进程           · 自己管监听、鉴权、gzip、反代
    ///   · 不改任何页面/API 内容
    ///
    /// ── 内部端口怎么选（本任务的选型说明）────────────────────────────
    ///   <see cref="AppSettings.WebInternalPort"/> 为 0 时自动推导：
    ///   从 <c>WebPort + 1</c> 起往上找第一个**能真正绑上 127.0.0.1** 的端口（最多试 20 个）。
    ///   为什么不写死：用户改 WebPort 时写死值必然不一致；为什么不用随机：
    ///   随机容易被防火墙/安全软件当成异常行为，且排查时不好认。
    ///
    /// ── 为什么不复用 IPC 让独立服务自己问端口 ────────────────────────
    ///   IPC 通道是主程序给「命令调用」用的（有长度上限、有并发语义），
    ///   拿它传一个进程启动参数属于杀鸡用牛刀，而且独立服务启动时序上必须先有端口
    ///   才能开始监听 —— 参数传递是最直接的。
    ///
    /// ── 进程管理 ────────────────────────────────────────────────────
    ///   · 由主程序拉起（隐藏窗口），与主程序同生命周期；
    ///   · 主程序退出 / 关闭网页 → 停掉子进程（不留孤儿占着端口）；
    ///   · 子进程崩溃 → 写 error.log 一条，不自动无限重启（避免崩溃循环刷日志）。
    /// </summary>
    internal static class WebHost
    {
        private static Process? _proc;
        private static int _externalPort;
        private static int _internalPort;
        private static int _warned;

        /// <summary>本机回环内部端口（0 = 未分配）。</summary>
        internal static int InternalPort => _internalPort;

        /// <summary>对外端口（0 = 未启用网页）。</summary>
        internal static int ExternalPort => _externalPort;

        /// <summary>独立服务是否已经在跑。</summary>
        internal static bool ServiceRunning
        {
            get
            {
                try { return _proc != null && !_proc.HasExited; }
                catch { return false; }
            }
        }

        /// <summary>
        /// 按配置启动网页入口。幂等：参数没变时什么都不做，变了会先停再起。
        /// </summary>
        /// <param name="externalPort">对外端口（= AppSettings.WebPort，0 = 不启动）。</param>
        /// <param name="configuredInternal">用户写死的内部端口（0 = 自动）。</param>
        /// <param name="password">网页口令（传给独立服务；独立服务也会自己读 config.json）。</param>
        internal static void Apply(int externalPort, int configuredInternal, string? password)
        {
            if (externalPort <= 0)
            {
                Stop();
                return;
            }
            if (externalPort < 1024 || externalPort > 65535)
            {
                LogBus.Warn("网页端口无效", "端口需在 1024~65535，当前 " + externalPort);
                Stop();
                return;
            }
            if (_externalPort == externalPort && _internalPort > 0 && ServiceRunning) return;

            Stop();

            _internalPort = configuredInternal > 0 ? configuredInternal : ResolveInternalPort(externalPort);
            if (_internalPort <= 0)
            {
                LogBus.Error("网页入口未启动", "找不到可用的回环内部端口（WebPort=" + externalPort + "）");
                return;
            }
            _externalPort = externalPort;

            // ① 主程序自己的回环 WebServer（页面/接口本体，内容一行未改）
            try
            {
                WebServer.Apply(_internalPort);
                if (WebServer.RunningPort != _internalPort)
                {
                    LogBus.Error("网页入口未启动", "主程序回环端口 " + _internalPort + " 绑定失败");
                    return;
                }
            }
            catch (Exception ex)
            {
                LogBus.Error("网页入口未启动", "回环 WebServer 异常：" + ex.Message);
                return;
            }

            // ①b ★ 统一凭据（2026-11-26）：先让主程序把 webAuthToken 落盘，
            //   再启动 TaskEdit。否则「用户设了口令、但还没登录过一次」的首次安装
            //   场景下，子进程读到的是空 token，主程序 /api/login 下发的 soullink_sid
            //   就永远穿不过代理（AuthGate 现在按 webAuthToken 校验 soullink_sid）。
            try { WebServer.EnsureAuthToken(); } catch { }

            // ② 独立服务（对外唯一入口）
            try
            {
                StartService(externalPort, _internalPort, password);
                LogBus.Ok("网页入口已就绪",
                    "对外 http://" + WebServer.LocalIp() + ":" + externalPort + "/  （内部回环 " + _internalPort + "）");
            }
            catch (Exception ex)
            {
                LogBus.Error("独立服务启动失败", ex.Message + "（页面仍可用 127.0.0.1:" + _internalPort + " 访问）");
            }
        }

        /// <summary>停止独立服务与回环 WebServer。</summary>
        internal static void Stop()
        {
            try
            {
                if (_proc != null && !_proc.HasExited)
                {
                    try { _proc.Kill(entireProcessTree: false); } catch { }
                    try { _proc.WaitForExit(3000); } catch { }
                }
            }
            catch { }
            finally
            {
                try { _proc?.Dispose(); } catch { }
                _proc = null;
            }
            try { WebServer.Stop(); } catch { }
            _externalPort = 0;
            _internalPort = 0;
        }

        /// <summary>
        /// 挑内部端口：**实现已移到 <see cref="WebServer.ResolveInternalPort"/>**（t36）。
        /// 移过去的原因：端口挑选与「网页服务本身」是同一件事，且那里可被
        /// tools/portguard-check 直接覆盖；本方法只做转发，保留以不破坏既有调用点。
        /// </summary>
        internal static int ResolveInternalPort(int externalPort) => WebServer.ResolveInternalPort(externalPort);
        /// <summary>独立服务 exe 路径：与主程序同目录（发布时一起放）。</summary>
        internal static string ServiceExePath
        {
            get
            {
                try { return Path.Combine(AppIdentity.ExeDir, "SoulLink.TaskEdit.exe"); }
                catch { return "SoulLink.TaskEdit.exe"; }
            }
        }

        private static void StartService(int externalPort, int internalPort, string password)
        {
            string exe = ServiceExePath;
            if (!File.Exists(exe))
            {
                // 开发机常见：还没 publish。给出明确指引而不是静默降级。
                LogBus.Warn("独立服务未安装",
                    "未找到 " + Path.GetFileName(exe) + "；对外端口 " + externalPort +
                    " 不会被监听（页面仍可用 127.0.0.1:" + internalPort + " 访问）");
                return;
            }

            // ★★ 先尝试**复用**已在运行的实例（t24 移交要求）★★
            //   场景：主程序重启（崩溃/用户重开），独立服务还活着并占着对外端口。
            //   这时如果直接 Process.Start 再起一个，新实例会因单实例 Mutex 直接 exit=5，
            //   对外端口仍然指向**旧的内部端口** → 全站「主程序未运行」。
            //   正确做法：探测到活的实例 → 只把新的内部端口告诉它（/taskedit/rebind）。
            if (TryReuseRunningInstance(externalPort, internalPort))
            {
                LogBus.Ok("复用在跑的网页服务", "已把上游切换到 127.0.0.1:" + internalPort);
                return;
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppIdentity.ExeDir,
            };
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(externalPort.ToString());
            psi.ArgumentList.Add("--internal-port");
            psi.ArgumentList.Add(internalPort.ToString());
            // 口令通过环境变量传，避免出现在进程命令行（命令行对同机其他用户可见）
            try
            {
                if (!string.IsNullOrWhiteSpace(password))
                    psi.Environment["SOULLINK_WEB_PASSWORD"] = password;
            }
            catch { }

            var proc = Process.Start(psi);
            if (proc == null) throw new InvalidOperationException("进程未启动");
            _proc = proc;
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(1200).ConfigureAwait(false); } catch { }
                try
                {
                    if (_proc != null && _proc.HasExited && Interlocked.Exchange(ref _warned, 1) == 0)
                        LogBus.Error("独立服务已退出",
                            "SoulLink.TaskEdit 启动后立即退出（exit=" + _proc.ExitCode +
                            "）。常见原因：端口 " + externalPort + " 被占用，或缺少管理员权限绑定 + 前缀。请查看 " +
                            Path.Combine(AppIdentity.DataDir, "error.log"));
                }
                catch { }
            });
        }

        /// <summary>
        /// 探测「对外端口上是否已有一个本服务的实例」，并让它把上游切到本次的内部端口。
        /// ★ 判据用 /taskedit/health 返回的 service 字段：不能只看「端口是否被监听」——
        ///   那可能是别人的服务（用户把网页端口填成了别的程序在用的端口）。
        /// ★ 只连 127.0.0.1：本机回环，不需要口令，也不会泄露到局域网。
        /// </summary>
        private static bool TryReuseRunningInstance(int externalPort, int internalPort)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1200) };
                string health;
                try
                {
                    health = probe.GetStringAsync("http://127.0.0.1:" + externalPort + "/taskedit/health")
                        .GetAwaiter().GetResult();
                }
                catch { return false; }   // 端口没人听 / 不是 HTTP / 超时

                if (health.IndexOf("SoulLink.TaskEdit", StringComparison.Ordinal) < 0) return false;

                // 是我们的服务 → 让它改指新的内部端口
                var body = new StringContent(
                    "{\"internalPort\":" + internalPort + "}",
                    System.Text.Encoding.UTF8, "application/json");
                using var post = new HttpClient { Timeout = TimeSpan.FromMilliseconds(1500) };
                using var resp = post.PostAsync(
                    "http://127.0.0.1:" + externalPort + "/taskedit/rebind", body)
                    .GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return false;

                // 复用成功：_proc 留空（不归我们管，退出时也不该去 Kill 别人的进程）
                _proc = null;
                _externalPort = externalPort;
                _internalPort = internalPort;
                return true;
            }
            catch { return false; }
        }
    }
}