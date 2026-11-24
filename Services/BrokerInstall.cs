using System;
using System.IO;
using System.Diagnostics;
using System.Text;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Security.Cryptography;

namespace SoulLink.Services
{
    /// <summary>固定测试版目录。Broker 只接受这个受系统 ACL 保护的位置。</summary>
    public static class BrokerInstall
    {
        public const string DirectoryName = "SoulLinkDev";
        public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DirectoryName);
        public static string ServiceExe => Path.Combine(Root, "SoulLink.exe");
        public static string AgentExe => Path.Combine(Root, "SoulLink.Agent.exe");
        public const string AgentVersionFileName = "SoulLink.Agent.version";
        /// <summary>主程序/服务要求的注入代理最低版本标记（内容 = 版本号单行）。与 agent 自身版本标记是不同文件。</summary>
        public const string RequiredAgentVersionFileName = "SoulLink.Agent.required.version";

        /// <summary>与 exe 同目录的随包 version 标记文件路径（内容 = 版本号单行）。</summary>
        public static string VersionFileFor(string exePath)
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) dir = Root;
            return Path.Combine(dir, AgentVersionFileName);
        }

        /// <summary>与 exe 同目录的「要求的 agent 最低版本」标记文件路径。</summary>
        public static string RequiredVersionFileFor(string exePath)
        {
            string? dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) dir = Root;
            return Path.Combine(dir, RequiredAgentVersionFileName);
        }

        /// <summary>Broker 通道代理的进程名（不带 .exe）。界面识别通道时要按它匹配。</summary>
        public static string AgentProcessName => Path.GetFileNameWithoutExtension(AgentExe);
        public static bool IsFixedTestPath(string? path) => string.Equals(
            Path.GetFullPath(path ?? ""), ServiceExe, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 注入代理是否满足投送要求。唯一判据入口：AgentSync.Inspect 也必须走这里，禁止自算一套。
        ///
        /// 判据 = 「agent 版本 >= 调用方要求的 agent 最低版本」，方向性（t7a 用户设计意图，2026-11-24）：
        ///   · agent 无标识 → false（需替换，走重铺平滑迁移）；
        ///   · 调用方（主程序/服务）无要求标识 → true（放行，不校验 agent 版本）；
        ///   · 双方都有 → 语义版本比较，agent 不得低于要求。
        ///
        /// ★ 基线 = 「调用方要求的 agent 版本」（参数 source 所在 exe 编译进的 &lt;AgentVersion&gt;，
        ///   由同目录 SoulLink.Agent.required.version 暴露），**不是服务版本、也不是 FileVersion**。
        ///   —— 用户 17:07：主程序检测到代理版本过低就主动替换；代理改得频繁。服务是否达标走
        ///   BrokerProtocol.MinimumServiceVersion 单独判，不在这里背锅。
        ///   服务本体自身版本再高（如 3.3.40）也不应让「要求的 agent 版本」跟着涨：那会让
        ///   独立 agent（1.0.0）永远被误判「过低」而重复重铺。
        /// </summary>
        public static bool ValidAgent(string source, string agent)
        {
            try
            {
                // ★ 路径校验保留：agent 必须落在受 ACL 保护的固定目录（不放开到任意路径）。
                if (!string.Equals(Path.GetFullPath(agent), AgentExe, StringComparison.OrdinalIgnoreCase)) return false;

                string? required = ReadRequiredAgentVersion(source);
                string? av = ReadAgentVersion(agent);

                if (string.IsNullOrEmpty(av)) return false;        // agent 无标识 → 需替换
                if (string.IsNullOrEmpty(required)) return true;   // 调用方无要求 → 不校验（放行）

                // 双方都有：agent 版本不得低于要求（语义版本比较）。
                if (!TryParseVersion(required, out var rVer) || !TryParseVersion(av, out var aVer)) return false;
                return aVer >= rVer;
            }
            catch { return false; }
        }

        /// <summary>
        /// 语义版本比较。先归一化（统一裁成三段，见 NormalizeVersion），解析失败返回 false（保守拒绝，宁可替换）。
        /// </summary>
        private static bool TryParseVersion(string? text, out Version ver)
        {
            ver = new Version(0, 0, 0, 0);
            string? normalized = NormalizeVersion(text);
            if (normalized == null) return false;
            return Version.TryParse(normalized, out ver!);
        }

        /// <summary>
        /// 版本段数归一化：FileVersion 是四段 "3.3.40.0"，AgentVersion 是三段 "1.0.0"，
        /// 直接比较会因段数不同判错。统一裁成最多三段（丢弃第四段 revision）。
        /// 写一处，ReadAgentVersion 与 TryParseVersion 共用。
        /// </summary>
        public static string? NormalizeVersion(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Trim().Split('.');
            var sb = new StringBuilder();
            int count = 0;
            foreach (var p in parts)
            {
                if (count >= 3) break;
                if (!int.TryParse(p, out int n)) break;
                if (count > 0) sb.Append('.');
                sb.Append(n);
                count++;
            }
            return count > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// 读「要求的 agent 最低版本」（调用方 exe 编译进的 &lt;AgentVersion&gt;）：
        ///   ① exe 同目录 SoulLink.Agent.required.version（t7a 起 csproj 写出）；
        ///   ② 托管元数据 AssemblyMetadata("AgentVersion")（非单文件/老构建）。
        ///   ★ 刻意**不回退 FileVersion**：调用方 FileVersion 是它自己的主程序版本，不是 agent 要求；
        ///     回退会把 agent 要求错绑到主程序版本，独立 agent 永远被判「过低」。
        ///   读不到返回 null —— ValidAgent 按「无要求 → 放行」处理。
        /// </summary>
        public static string? ReadRequiredAgentVersion(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                string marker = RequiredVersionFileFor(path);
                if (File.Exists(marker))
                {
                    try
                    {
                        string? v = NormalizeVersion(File.ReadAllText(marker));
                        if (v != null) return v;
                    }
                    catch { }
                }

                string? meta = ReadAssemblyMetadataAgentVersion(path);
                return NormalizeVersion(meta);
            }
            catch { return null; }
        }

        /// <summary>
        /// 读 AgentVersion，三级回退（P0-1 修复，3.3.40）：
        ///   ① exe 同目录的随包 version 文件（SoulLink.Agent.version，单行）；
        ///   ② 托管元数据 AssemblyMetadata("AgentVersion")（老版本/非单文件时有效）；
        ///   ③ FileVersion（单文件下实测可读，四段，先归一化成三段）。
        ///   都读不到返回 null —— 调用方（ValidAgent）按「agent 无标识=需替换 / 服务无标识=放行」方向处理。
        ///   不加载程序集、不锁文件、不受「同 identity 已加载」干扰。
        /// </summary>
        public static string? ReadAgentVersion(string path) => TryReadAgentVersion(path, out _);

        /// <summary>
        /// 与 ReadAgentVersion 相同，但额外返回本次命中哪一级回退（供 AgentSync 工具提示 / t2 复用）。
        /// source = "version-file" | "metadata" | "fileversion" | "none"。
        /// </summary>
        public static string? TryReadAgentVersion(string path, out string source)
        {
            source = "none";
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

                // ① 随包 version 文件（内容 = 版本号单行）。安装目录里的老 agent 没有它，回退到 ②③。
                string marker = VersionFileFor(path);
                if (File.Exists(marker))
                {
                    try
                    {
                        string? v = NormalizeVersion(File.ReadAllText(marker));
                        if (v != null) { source = "version-file"; return v; }
                    }
                    catch { }
                }

                // ② 托管元数据 AssemblyMetadata("AgentVersion")。单文件发布下 HasMetadata=False，读不到就回退 ③。
                string? meta = ReadAssemblyMetadataAgentVersion(path);
                string? metaV = NormalizeVersion(meta);
                if (metaV != null) { source = "metadata"; return metaV; }

                // ③ FileVersion（Win32 版本资源，单文件发布保留，实测可读）。四段归一化为三段。
                string? fv = null;
                try { fv = FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { }
                string? fvV = NormalizeVersion(fv);
                if (fvV != null) { source = "fileversion"; return fvV; }

                return null;
            }
            catch { return null; }
        }

        /// <summary>用 System.Reflection.Metadata 读 PE 元数据 AssemblyMetadata("AgentVersion")，读不到返回 null。</summary>
        private static string? ReadAssemblyMetadataAgentVersion(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata) return null;
                var md = pe.GetMetadataReader();
                foreach (var caHandle in md.GetAssemblyDefinition().GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(caHandle);
                    if (ca.Constructor.Kind != HandleKind.MemberReference) continue;
                    var ctorRef = md.GetMemberReference((MemberReferenceHandle)ca.Constructor);
                    if (ctorRef.Parent.Kind != HandleKind.TypeReference) continue;
                    var typeRef = md.GetTypeReference((TypeReferenceHandle)ctorRef.Parent);
                    if (!string.Equals(md.GetString(typeRef.Name), "AssemblyMetadataAttribute", StringComparison.Ordinal)) continue;

                    var br = md.GetBlobReader(ca.Value);
                    if (br.ReadUInt16() != 1) continue;          // custom attribute Prolog
                    if (!br.TryReadCompressedInteger(out int keyLen)) continue;
                    var key = br.ReadUTF8(keyLen);
                    if (!br.TryReadCompressedInteger(out int valLen)) continue;
                    var val = br.ReadUTF8(valLen);
                    if (string.Equals(key, "AgentVersion", StringComparison.OrdinalIgnoreCase)) return val;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>单文件发布的 BaseDirectory 可能是临时目录，必须以真实 exe 旁的工具为准。</summary>
        public static string FindScript(string action)
        {
            string exe = SystemLauncher.ResolveSelfExe();
            string directory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            string script = Path.Combine(directory, "tools", "broker-" + action + ".ps1");
            if (File.Exists(script)) return Path.GetFullPath(script);
            string local = Path.Combine(AppContext.BaseDirectory, "broker-" + action + ".ps1");
            return File.Exists(local) ? Path.GetFullPath(local) : script;
        }

        public static string AgentFor(string source)
        {
            if (!IsFixedTestPath(source)) return source;
            if (!File.Exists(AgentExe) || !ValidAgent(source, AgentExe)) return "";
            return AgentExe;
        }
    }
}
