using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SoulLink.Services
{
    /// <summary>测试版 Broker 协议。只允许部署本程序签发的管理员输入代理。</summary>
    public static class BrokerProtocol
    {
        public const string ServiceName = "SoulLink.DevBroker";
        public const string PipeName = "SoulLink.DevBroker.v1";
        public const int Version = 1;

        public sealed class Request
        {
            public int Version { get; set; } = BrokerProtocol.Version;
            public string Command { get; set; } = "health";
            public int SessionId { get; set; }
            public string UserName { get; set; } = "";
            public string PipeName { get; set; } = "";
            public string Nonce { get; set; } = "";
            public int Pid { get; set; }

            /// <summary>
            /// 需要注入给目标进程的环境变量。
            /// ★ 3.2.8：录制回放要写主程序目录，而代理运行在 Program Files，
            ///   自己推不出主程序位置，必须由投送方告知。
            ///   旧版 Broker 不认识这个字段（反序列化后为 null），会在服务端忽略。
            /// </summary>
            public Dictionary<string, string>? Env { get; set; }
        }

        public sealed class Response
        {
            public bool Ok { get; set; }
            public string Code { get; set; } = "";
            public string Message { get; set; } = "";
            public int Pid { get; set; }
            public int SessionId { get; set; }
            public int Version { get; set; } = BrokerProtocol.Version;

            /// <summary>
            /// 服务端**程序**版本（如 "2.11.0.0"）。
            ///
            /// ★ 与 <see cref="Version"/>（协议版本）是两件事：
            ///   协议版本只保证"报文格式能互相读懂"，程序版本才反映功能是否配套。
            ///   用户实测：Broker 服务跑 2.11.0、客户端 2.17.11，
            ///   协议都是 v1 所以健康检查显示"可用"，但投送全部失败。
            ///
            /// ★ 旧版服务没有这个字段 → 反序列化得到空串，视为"未知"。
            /// </summary>
            public string AppVersion { get; set; } = "";
        }

        /// <summary>本程序的 FileVersion（"2.17.11.0"）。取不到就返回空串。</summary>
        public static string SelfAppVersion
        {
            get
            {
                try
                {
                    var p = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        return FileVersionInfo.GetVersionInfo(p).FileVersion ?? "";
                }
                catch { }
                try
                {
                    var asm = typeof(BrokerProtocol).Assembly;
                    return asm.GetName().Version?.ToString(4) ?? "";
                }
                catch { }
                return "";
            }
        }

        /// <summary>
        /// 取 Broker 服务实际运行的 exe 版本。
        ///
        /// ★ 为什么不只靠 health 响应里的 AppVersion：
        ///   旧版服务（例如 2.11.0）根本不认识那个字段，永远返回空，
        ///   于是"版本不一致"永远检测不出来 —— 正是用户当前遇到的情况。
        ///   直接读服务 exe 的文件版本对旧服务同样有效。
        /// </summary>
        public static string ServerExeVersion()
        {
            try
            {
                // 服务配置里的 BINARY_PATH_NAME 指向真实 exe
                string path = BrokerInstall.ServiceExe;
                if (File.Exists(path))
                    return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";

                // 兜底：查服务注册的路径
                using var service = new ServiceController(ServiceName);
                string? registered = service.ServiceName;  // 触发一次访问，服务不存在会抛
                _ = registered;
            }
            catch { }
            return "";
        }

        // ══════════════════════════════════════════════════════════════════
        //  最低可用服务版本
        // ══════════════════════════════════════════════════════════════════
        //
        // ★ 设计意图（用户提出）：不再要求"服务版本 == 客户端版本"
        //   （那会让每次客户端升级都必须重装服务），改为两端共同遵守
        //   一个**最低可用版本**：只要服务 >= 这个版本就算兼容。
        //
        // ★ 这个常量是"单一真源"：
        //   · 客户端用它判断"这台机器上的 Broker 够不够新"
        //   · 服务端用它判断"我这个版本是否还该被接受"
        //   两端引用同一个值，永远不会对"什么算兼容"产生分歧。
        //
        // ★ 何时抬高它：只有在**协议/行为发生不兼容变化**时才改。
        //   仅仅加了新功能但旧报文仍然可用时，不该抬高（否则又要全员重装服务）。

        /// <summary>
        /// Broker 服务的最低可用程序版本。低于此版本的服务的投送请求会被拒绝。
        ///
        /// ★ 注意：这里填的是**版本号**，不是协议 Version。
        ///   2.17.12 起启用该检查；2.11.0 及更早的旧服务不认识此机制，
        ///   客户端通过读服务 exe 文件版本自行判断（对旧服务同样有效）。
        /// </summary>
        public const string MinimumServiceVersion = "2.17.13.0";

        /// <summary>
        /// 把 "2.17.12.0" 这类版本串解析成可比较的四段数字。
        /// 解析失败返回 null（调用方应视为"无法判断"）。
        /// </summary>
        public static Version? ParseVersion(string? text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return null;
                string s = text.Trim();

                // 去掉可能的 "+提交号" / " (xxx)" 之类后缀
                int cut = s.IndexOfAny(new[] { '+', ' ', '(', '（' });
                if (cut > 0) s = s.Substring(0, cut);

                if (System.Version.TryParse(s, out var v)) return v;

                // "2.17" 这种两段式，补齐成四段
                var parts = s.Split('.');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out int a)
                    && int.TryParse(parts[1], out int b))
                    return new Version(a, b, 0, 0);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 判断给定服务版本是否满足最低要求。
        /// 返回 (是否达标, 给用户看的说明)。说明为 null 表示没有问题。
        /// </summary>
        public static (bool ok, string? message) CheckServiceVersion(string serviceVersion)
        {
            var min = ParseVersion(MinimumServiceVersion);
            var actual = ParseVersion(serviceVersion);

            if (min == null) return (true, null);          // 常量写错不该阻断用户
            if (actual == null)
                return (true, null);                       // 判断不出来就不下结论

            if (actual >= min) return (true, null);

            return (false,
                "Broker 服务版本 " + serviceVersion + " 低于最低要求 " + MinimumServiceVersion
              + "。\n\n"
              + "请到「设置 → 投送方式 → 安装 / 管理服务」更新后台服务，"
              + "或暂时改用「SYSTEM 计划任务」通道。");
        }

        /// <summary>
        /// 比对客户端与 Broker 服务的版本，给出提示或拦截理由。
        ///
        /// 返回 null = 可正常使用；否则返回给用户看的说明。
        /// ★ 只判断"是否低于最低可用版本"，**不要求与服务版本完全一致**
        ///   （用户要求：换个方式，用最低版本门槛，避免每次升级都要重装服务）。
        /// ★ 服务版本优先取 health 响应；没有则读服务 exe 文件版本（兼容旧服务）。
        /// </summary>
        public static string? DescribeAppVersionMismatch(Response response)
        {
            try
            {
                string server = (response.AppVersion ?? "").Trim();
                if (server.Length == 0) server = ServerExeVersion();
                if (server.Length == 0) return null;       // 真的判断不出来

                var (ok, message) = CheckServiceVersion(server);
                if (!ok) return message;

                // ★ 达到最低可用版本 → **一律视为正常，不再提示更新**。
                //
                //   用户明确要求：
                //   「服务大于等于最低可用版本，就不应该让用户去更新，太麻烦了」。
                //
                //   原来这里还会在"服务版本低于客户端版本"时弹一条琥珀提示
                //   （即便服务远高于最低门槛），结果是每升一次客户端就催一次重装服务 ——
                //   而重装服务本身会断开所有已投送的代理，代价远大于收益。
                //
                //   现在的判定与投送闸门完全一致：
                //     · 低于 MinimumServiceVersion → 拦截并说明（真正不兼容）
                //     · 达到该版本 → 正常使用，不提示
                return null;
            }
            catch { return null; }
        }

        public static PipeSecurity AdministratorPipeSecurity()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            security.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return security;
        }

        /// <summary>
        /// 测试版与挂机验证版各自的代理管道。身份前缀必须完整匹配，不能因此共用
        /// 命名管道或放宽会话号 / 随机后缀；其余 nonce 与进程身份校验保持不变。
        /// </summary>
        public static bool ValidPipeName(string? value) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= 160
            && System.Text.RegularExpressions.Regex.IsMatch(value,
                @"^(?:SoulLink|SoulLink-Stable)_Relay_[1-9][0-9]*_[0-9a-f]{8}$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>缓存的"服务版本是否达标"结果，避免每条请求都读文件。</summary>
        private static bool? _serviceVersionOk;
        private static DateTime _serviceVersionCheckedAt = DateTime.MinValue;

        /// <summary>
        /// 让版本闸门缓存失效。
        /// ★ 服务刚被更新过时必须调用：否则 30 秒内仍按"旧版本不可用"拦投送，
        ///   用户会以为"装了服务也没用"（用户实测过这个感受）。
        /// </summary>
        public static void InvalidateServiceVersionCache()
        {
            _serviceVersionOk = null;
            _serviceVersionCheckedAt = DateTime.MinValue;
        }

        /// <summary>
        /// 投送前的最低版本闸门（用户要求：用最低可用版本，而不是要求完全相同）。
        /// ★ 只拦"低于最低版本"的服务；高于或等于一律放行。
        /// ★ 结果缓存 30 秒：服务可能被就地更新，不要永久记住坏结果。
        /// </summary>
        private static bool EnsureServiceVersionOk(out string message)
        {
            message = "";
            try
            {
                if (_serviceVersionOk.HasValue
                    && (DateTime.UtcNow - _serviceVersionCheckedAt).TotalSeconds < 30)
                {
                    if (_serviceVersionOk.Value) return true;
                    var (_, cached) = CheckServiceVersion(ServerExeVersion());
                    message = cached ?? "";
                    return _serviceVersionOk.Value;
                }

                string version = ServerExeVersion();
                var (ok, msg) = CheckServiceVersion(version);
                _serviceVersionOk = ok;
                _serviceVersionCheckedAt = DateTime.UtcNow;
                if (!ok) message = msg ?? ("Broker 服务版本 " + version + " 过低");
                return ok;
            }
            catch { return true; }   // 判断不出来就不阻断
        }

        public static async Task<Response> SendAsync(Request request, int timeoutMs = 12000)
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                if (service.Status != ServiceControllerStatus.Running)
                    return Fail("NOT_RUNNING", "Broker 服务已安装但未运行（状态：" + service.Status + "）");

                // ★ 最低版本闸门：只对**投送类**命令生效。
                //   health 必须放行 —— 否则界面连"服务版本过低"这个信息都拿不到。
                if (request.Command != "health" && !EnsureServiceVersionOk(out string verMsg))
                    return Fail("SERVICE_OUTDATED", verMsg);
                using var timeout = new CancellationTokenSource(timeoutMs);
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                pipe.Connect(timeoutMs);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                string payload = JsonSerializer.Serialize(request) + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                string? line = reader.ReadLine();
                if (line == null) return Fail("BROKER_CLOSED", "Broker 关闭了连接");
                return JsonSerializer.Deserialize<Response>(line) ?? Fail("BAD_RESPONSE", "Broker 返回空响应");
            }
            catch (InvalidOperationException) { return Fail("NOT_INSTALLED", "Broker 服务未安装；请点击「安装 / 修复服务」"); }
            catch (System.ComponentModel.Win32Exception) { return Fail("NOT_INSTALLED", "Broker 服务未安装；请点击「安装 / 修复服务」"); }
            catch (System.TimeoutException) { return Fail("NOT_INSTALLED", "Broker 服务已安装，但管道未就绪"); }
            catch (OperationCanceledException) { return Fail("TIMEOUT", "Broker 服务已安装，但健康检查超时"); }
            catch (Exception ex)
            {
                if (ex is System.TimeoutException) return Fail("NOT_INSTALLED", "测试版 Broker 未安装或未运行");
                if (ex is OperationCanceledException) return Fail("TIMEOUT", "Broker 已连接，但未在限定时间内响应");
                return Fail("UNAVAILABLE", "Broker 不可用：" + ex.Message);
            }
        }

        public static Response Fail(string code, string message) => new() { Ok = false, Code = code, Message = message };

        /// <summary>
        /// Broker 服务被安装 / 更新后触发，供中控台重置投送状态并重新投送。
        /// ★ 订阅方需自行 marshal 到 UI 线程。
        /// </summary>
        public static event Action? ServiceUpdated;

        /// <summary>通知"Broker 服务已更新"。异常不外抛，绝不影响安装流程。</summary>
        public static void NotifyServiceUpdated()
        {
            try { ServiceUpdated?.Invoke(); } catch { }
        }
    }

    /// <summary>供 SYSTEM 服务和已提权主程序共用的 Win32 身份检查。</summary>
    public static class BrokerSecurity
    {
        public sealed class ProcessIdentity
        {
            public int SessionId;
            public string User = "";
            public bool Elevated;
            public bool Administrator;
            public string Sid = "";
        }

        public static ProcessIdentity InspectProcess(int pid)
        {
            var identity = new ProcessIdentity();
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess PID=" + pid);
            try
            {
                if (!ProcessIdToSessionId(pid, out uint session))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "ProcessIdToSessionId PID=" + pid);
                identity.SessionId = (int)session;
                if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken PID=" + pid);
                try
                {
                    // ★ 只用 P/Invoke 读令牌：WindowsIdentity(IntPtr) 在 SYSTEM 里会尝试
                    //   把令牌转成主令牌（需要 SeTcbPrivilege），失败就抛“拒绝访问”。
                    identity.Sid = ReadUserSid(token);
                    if (identity.Sid == "S-1-5-18") { identity.User = "SYSTEM"; identity.Administrator = true; identity.Elevated = true; return identity; }
                    identity.Elevated = TokenElevation(token);
                    identity.Administrator = IsAdministrator(token);
                    identity.User = ReadUserName(identity.Sid, identity.Administrator);
                }
                finally { CloseHandle(token); }
                return identity;
            }
            finally { CloseHandle(process); }
        }

        public static ProcessIdentity InspectToken(IntPtr token)
        {
            string sid = ReadUserSid(token);
            return new ProcessIdentity { Elevated = TokenElevation(token), Sid = sid, Administrator = IsAdministrator(token), User = ReadUserName(sid, IsAdministrator(token)) };
        }

        /// <summary>
        /// 只读令牌里的用户 SID（TokenUser）。
        /// ★ 绝不要在这里 new WindowsIdentity(token)：SYSTEM 进程里那需要 SeTcbPrivilege，
        ///   拿不到就抛“拒绝访问”，历史上正是它让整条投送链路都以 AUTH_FAIL 结束。
        /// </summary>
        public static string ReadUserSid(IntPtr token)
        {
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out int size))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ERROR_INSUFFICIENT_BUFFER) throw new System.ComponentModel.Win32Exception(error, "TokenUser 长度");
                if (size <= 0) return "";
            }
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, size, out _)) return "";
                IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
                return sidPointer == IntPtr.Zero ? "" : ConvertSid(sidPointer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        /// <summary>令牌主体名。取不到名字时退回 SID 文本，绝不抛异常。</summary>
        private static string ReadUserName(string sid, bool administrator)
        {
            if (string.IsNullOrEmpty(sid)) return administrator ? "Administrators" : "";
            if (sid == "S-1-5-18") return "SYSTEM";
            try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
            catch { return sid; }
        }

        public static bool TokenElevation(IntPtr token)
        {
            IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, buffer, sizeof(int), out _))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "TokenElevation");
                return Marshal.ReadInt32(buffer) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public static string DescribeToken(IntPtr token)
        {
            string sid = ReadUserSid(token);
            bool elevated = TokenElevation(token);
            bool admin = IsAdministrator(token);
            return ReadUserName(sid, admin) + " elevated=" + elevated + " admin=" + admin + " sid=" + sid;
        }

        /// <summary>
        /// 令牌是否属于管理员组。
        ///
        /// ★ 不要用 WindowsPrincipal.IsInRole：它走 LSA 查询，在 SYSTEM 服务进程里需要
        ///   SeTcbPrivilege，拿不到就抛 SecurityException“拒绝访问”（实测 100% 复现）。
        ///   也不要传 token 句柄给 CheckTokenMembership：它会先复制令牌，同样被拒。
        ///   这里改为直接扫描 TokenGroups 里的 BUILTIN\Administrators，纯本地读令牌，不依赖特权。
        /// </summary>
        public static bool IsAdministrator(IntPtr token)
        {
            try
            {
                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenGroups, IntPtr.Zero, 0, out int size))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_INSUFFICIENT_BUFFER || size <= 0) return false;
                }
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenGroups, buffer, size, out _)) return false;
                    var header = Marshal.PtrToStructure<TOKEN_GROUPS_HEADER>(buffer);
                    if (header.GroupCount < 0 || header.GroupCount > 4096) return false;
                    IntPtr entry = buffer + (int)Marshal.OffsetOf<TOKEN_GROUPS_HEADER>(nameof(TOKEN_GROUPS_HEADER.Groups));
                    for (int i = 0; i < header.GroupCount; i++, entry += Marshal.SizeOf<TOKEN_GROUPS_ENTRY>())
                    {
                        IntPtr sidPointer = Marshal.ReadIntPtr(entry);
                        if (sidPointer == IntPtr.Zero) continue;
                        if (ConvertSid(sidPointer) == AdministratorsSid) return true;
                    }
                    return false;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch { return false; }
        }

        private static string ConvertSid(IntPtr sidPointer)
        {
            int subAuthorityCount = Marshal.ReadByte(sidPointer + 1);
            int length = 8 + 4 * subAuthorityCount;
            byte[] raw = new byte[length];
            Marshal.Copy(sidPointer, raw, 0, length);
            return new SecurityIdentifier(raw, 0).Value;
        }

        /// <summary>Broker 客户端认证：返回失败原因（null 表示通过），永不抛异常，便于逐项记录。</summary>
        public static string? ValidateClient(NamedPipeServerStream pipe, bool allowSystem, out int clientPid)
        {
            clientPid = 0;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out nuint rawPid))
                return "无法验证 Broker 客户端 PID：" + Marshal.GetLastWin32Error();
            clientPid = checked((int)rawPid);
            ProcessIdentity identity;
            try { identity = InspectProcess(clientPid); }
            catch (Exception ex) { return "读取客户端令牌失败：" + ex.Message; }
            if (allowSystem && identity.Sid == SystemSid) return null;
            if (!identity.Administrator) return "客户端不在管理员组（SID " + identity.Sid + "）";
            if (!identity.Elevated) return "客户端未提权（ElevationType 受限）";
            return null;
        }

        public static string DescribeProcess(int pid)
        {
            try
            {
                var identity = InspectProcess(pid);
                return "pid=" + pid + " user=" + identity.User + " sid=" + identity.Sid
                       + " session=" + identity.SessionId + " admin=" + identity.Administrator + " elevated=" + identity.Elevated;
            }
            catch (Exception ex) { return "pid=" + pid + " 身份读取失败：" + ex.Message; }
        }

        public const string SystemSid = "S-1-5-18";
        private const string AdministratorsSid = "S-1-5-32-544";
        private const uint TOKEN_QUERY = 0x0008;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ERROR_INSUFFICIENT_BUFFER = 122;
        private enum TOKEN_INFORMATION_CLASS { TokenUser = 1, TokenGroups = 2, TokenElevation = 20 }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_GROUPS_HEADER
        {
            public int GroupCount;
            public IntPtr Groups;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_GROUPS_ENTRY
        {
            public IntPtr Sid;
            public int Attributes;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out uint session);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls, IntPtr info, int length, out int returned);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out nuint pid);
    }
}
