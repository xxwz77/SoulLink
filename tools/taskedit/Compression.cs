using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SoulLink.TaskEdit
{
    /// <summary>
    /// gzip 响应压缩（设计文档 §17.3）。
    ///
    /// ── 现有状况 ───────────────────────────────────────────────────────
    ///   主程序 WebServer 完全没有 gzip（设计文档 grep 0 命中），这是
    ///   「远程体验不一致」的最大来源：10 MB 的配置/路线 JSON 明文过公网。
    ///
    /// ── 本类的两条硬规则 ──────────────────────────────────────────────
    ///   ① **只压文本**：JSON / HTML / CSS / JS / 纯文本。判断依据**先看
    ///      Content-Type**（不看扩展名）—— 同一个路径可能返回 JSON 也可能返回视频。
    ///   ② **绝不压流式与大二进制**：视频（video/*）、图片、录像、字体、
    ///      <c>Content-Range</c> 存在的 206 分片响应、以及 <c>Content-Length</c>
    ///      未知的 chunked 流，一律原样透传。
    ///      理由：压缩会破坏 Range 语义（客户端要的是原始字节偏移），
    ///      而且视频本来就不压缩，套一层 gzip 只会浪费 CPU 并让播放器判定失败。
    ///
    /// ── 为什么不压「已压缩」类型 ──────────────────────────────────────
    ///   jpg/png/webp/mp4/zip/br/gz 本身已压缩，再压一遍通常更大。
    /// </summary>
    internal static class Compression
    {
        /// <summary>
        /// 低于这个字节数不压。
        ///
        /// ★ 阈值取 512（t24 移交要求）。原因：gzip 固定开销约 18 字节
        ///   （10 字节头 + 8 字节尾），但**小响应的压缩率极差** ——
        ///   一段 300 字节的极简 JSON（如 <c>{"ok":true}</c> 加几个字段）
        ///   压完常常只小几十字节，甚至因为 deflate 块头而变大。
        ///   省下的那点带宽远不值得一次额外压缩 + 一次额外的解压。
        /// ★ 对 <c>/api/status</c> 这类极简响应，512 能确保它们**不被无意义地压**；
        ///   真正大的 JSON（KB 级以上）压缩率仍在 90%+（实测 4086→146）。
        /// </summary>
        internal const int MinBytes = 512;

        private static readonly HashSet<string> CompressibleTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/json", "application/javascript", "application/x-javascript",
            "application/xml", "application/xhtml+xml", "application/manifest+json",
            "image/svg+xml", "text/html", "text/css", "text/javascript", "text/plain",
            "text/xml", "text/csv", "application/ld+json"
        };

        /// <summary>客户端是否接受 gzip（解析 Accept-Encoding，忽略 q=0）。</summary>
        internal static bool ClientAcceptsGzip(string acceptEncoding)
        {
            if (string.IsNullOrWhiteSpace(acceptEncoding)) return false;
            foreach (string part in acceptEncoding.Split(','))
            {
                string item = part.Trim();
                if (item.Length == 0) continue;
                int semi = item.IndexOf(';');
                string coding = (semi >= 0 ? item.Substring(0, semi) : item).Trim();
                string rest = semi >= 0 ? item.Substring(semi + 1) : "";
                bool gzip = coding.Equals("gzip", StringComparison.OrdinalIgnoreCase)
                         || coding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase)
                         || coding.Equals("*", StringComparison.OrdinalIgnoreCase);
                if (!gzip) continue;
                if (rest.IndexOf("q=0", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // 明确拒绝
                return true;
            }
            return false;
        }

        /// <summary>Content-Type 是否属于可压缩文本。</summary>
        internal static bool IsCompressibleType(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return false;
            string type = contentType.Trim();
            int semi = type.IndexOf(';');
            if (semi >= 0) type = type.Substring(0, semi);
            type = type.Trim();
            if (CompressibleTypes.Contains(type)) return true;
            // text/* 一律可压（text/event-stream 除外：那是长连接流）
            if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !type.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)) return true;
            // +json / +xml 后缀族
            if (type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) return true;
            if (type.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 综合判定这次响应要不要压。
        /// <paramref name="contentRange"/> 非空 = 206 分片，必须原样透传。
        /// <paramref name="contentLength"/> &lt; 0 = 长度未知（chunked 流），不压。
        /// </summary>
        internal static bool ShouldCompress(string acceptEncoding, string contentType,
            string contentRange, long contentLength, bool isHead)
        {
            if (!ClientAcceptsGzip(acceptEncoding)) return false;
            if (!string.IsNullOrEmpty(contentRange)) return false;          // Range 分片不压
            if (contentLength >= 0 && contentLength < MinBytes) return false;
            if (contentLength < 0) return false;                            // 长度未知视为流，不压
            return IsCompressibleType(contentType);
        }

        /// <summary>
        /// 把一段完整字节压成 gzip。只用于**已知长度、已完整读入**的文本响应；
        /// 流式响应永远不走这里（见 <see cref="ShouldCompress"/> 的前置条件）。
        /// </summary>
        internal static byte[] Gzip(byte[] plain)
        {
            if (plain == null || plain.Length == 0) return plain ?? Array.Empty<byte>();
            using var output = new MemoryStream(plain.Length / 3 + 64);
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(plain, 0, plain.Length);
            return output.ToArray();
        }

        /// <summary>
        /// 把流边读边压进目标流（仅在被明确判定为「可压且长度已知」时使用）。
        /// </summary>
        internal static void GzipTo(Stream source, Stream target)
        {
            using var gz = new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true);
            byte[] buf = new byte[64 * 1024];
            int read;
            while ((read = source.Read(buf, 0, buf.Length)) > 0)
                gz.Write(buf, 0, read);
        }
    }
}
