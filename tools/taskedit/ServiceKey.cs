using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// 主程序 ↔ 独立服务之间的**进程间共享密钥**（Lead 2026-11-25 要求）。
    ///
    /// ── 为什么需要它（回环不够用）──────────────────────────────────────
    ///   `/taskedit/rebind` 原来只校验「来源是回环」。但**本机上任何普通权限进程**
    ///   （别的软件、某个脚本）同样来自回环，可以调用
    ///   `POST /taskedit/rebind?internalPort=9999` 把网页上游指向它自己的服务，
    ///   之后所有 `/api/*`（含米游社数据、账号状态）就都发到陌生进程手里了。
    ///   这是「本机进程间信任」假设带来的经典风险 —— 回环只证明"在同一台机器"，
    ///   不证明"是我们自己的主程序"。
    ///
    /// ── 两层防御（不是替代）───────────────────────────────────────────
    ///   ① **本类**：X-TaskEdit-Key 定长比较（`CryptographicOperations.FixedTimeEquals`）；
    ///   ② **回环限制继续保留**：即使 key 泄露到局域网也无法利用。
    ///   两层都过才允许 rebind。
    ///
    /// ── 生命周期（key 不落盘）─────────────────────────────────────────
    ///   主程序每次启动生成 32 字节随机 key，经**环境变量** `SOULLINK_TASKEDIT_KEY`
    ///   传给子进程（不进命令行 —— 命令行对同机其他进程可见）。
    ///   **不写 config.json、不写日志**，进程退出即失效。
    ///
    /// ── 主程序重启后怎么办（方案 b：不复用，直接换新的）─────────────────
    ///   key 一变，还在跑的旧实例就无法通过校验了。所以主程序重启时**不再复用**
    ///   旧实例，而是先把它干净结束、再起一个新实例（见 `Services/WebHost`）。
    ///   理由：网页编辑是短操作，被打断成本低；而若把 key 持久化到 config.json，
    ///   等于把它变成一个长期秘密，"进程退出即失效"的优点就没了。
    /// </summary>
    internal sealed class ServiceKey
    {
        internal const string EnvName = "SOULLINK_TASKEDIT_KEY";
        internal const string HeaderName = "X-TaskEdit-Key";

        private readonly byte[] _expected;

        internal ServiceKey(string? value)
        {
            string key = (value ?? "").Trim();
            _expected = key.Length == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(key);
        }

        /// <summary>从环境变量读取（子进程侧）。</summary>
        internal static ServiceKey FromEnvironment()
            => new(Environment.GetEnvironmentVariable(EnvName));

        /// <summary>生成一个新的 32 字节随机 key（主程序侧，Base64 32 字符以上）。</summary>
        internal static string Generate()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator.Fill(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>本实例是否已配置 key（未配置时所有受保护端点一律拒绝）。</summary>
        internal bool Configured => _expected.Length > 0;

        /// <summary>
        /// 校验请求头里的 key。
        /// ★ 用 `FixedTimeEquals` 定长比较，避免计时侧信道（与 <see cref="AuthGate"/> 同一做法）。
        /// ★ 未配置 key 时**一律返回 false** —— 绝不因为"没设 key 就放行"，
        ///   那正是主程序老代码 `PasswordEnabled()==false → return true` 犯过的错。
        /// </summary>
        internal bool Matches(HttpListenerContext ctx)
        {
            if (_expected.Length == 0) return false;
            string supplied;
            try { supplied = ctx.Request.Headers[HeaderName] ?? ""; }
            catch { return false; }
            if (supplied.Length == 0) return false;
            byte[] actual = Encoding.UTF8.GetBytes(supplied);
            return actual.Length == _expected.Length && CryptographicOperations.FixedTimeEquals(actual, _expected);
        }
    }
}
