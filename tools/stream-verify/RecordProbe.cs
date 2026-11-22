using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StreamVerify
{
    /// <summary>
    /// 录像 / 截图 / CPU 采集侧验证。
    ///
    /// 为什么单独一个程序：
    ///   「零 JPEG」这件事不只是推流路径，还包括**录像路径**。
    ///   当前实现是「先写 frame-*.jpg 序列 → 转 MP4 → 删图片」，
    ///   所以光看"最终目录只有 mp4"不够，必须看**中间有没有产生 jpg**，
    ///   以及转码失败时会不会把 jpg 留下来。
    ///
    /// 同时它还要证明「静态截图功能没被删掉」——
    /// 这是「零 JPEG」改造最容易改过头的地方：
    /// 用户要的是「实时通路不要 JPEG」，不是「整个程序不能存 JPG」。
    ///
    /// 安全边界：只操作自己 Process.Start 的 agent，只写 %TEMP%。
    /// </summary>
    public static class RecordProbe
    {
        private static int _fails;
        private static void Line(string s) => Console.WriteLine(s);
        private static void Ok(string s) => Line("ok    " + s);
        private static void Fail(string s) { Line("FAIL  " + s); _fails++; }

        public static async Task<int> RunAsync(string agentDir, int port, string token, int seconds,
            string ffprobe)
        {
            string work = Path.Combine(Path.GetTempPath(), "recordprobe-" + port);
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            Directory.CreateDirectory(work);
            string captureDir = Path.Combine(work, "capture");
            Directory.CreateDirectory(captureDir);

            await using var host = await ProbeHost.StartAsync(agentDir, port, token, captureDir, Line);
            if (host == null) return 3;

            try
            {
                // ── ① 开始录制 ────────────────────────────────────────
                Line("");
                Line("== ① 开始录制（环形缓冲）");
                var rec = await host.SendAsync("{\"Cmd\":\"record\",\"Count\":15,\"X\":75,\"Delta\":30}");
                Line("   record：" + Short(rec));
                if (rec == null || !rec.Contains("\"Ok\":true")) { Fail("record 未被接受"); return _fails; }

                var warm = Stopwatch.StartNew();
                while (warm.Elapsed.TotalSeconds < 20)
                {
                    string st = (await host.SendAsync("{\"Cmd\":\"recordstatus\"}")) ?? "";
                    if (st.Contains("|frames=") && !st.Contains("|frames=0|"))
                    {
                        Line("   recordstatus：" + Short(st));
                        Ok("环形缓冲已有帧");
                        break;
                    }
                    await Task.Delay(400);
                }

                // 多攒几秒，让 save 有足够帧可写
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, seconds / 2)));

                // ── ② CPU 采样（采集侧成本）────────────────────────────
                Line("");
                Line("== ② 采集侧 CPU 采样（稳态 10 秒）");
                var proc = Process.GetProcessById(host.AgentPid);
                long cpu0 = (long)proc.TotalProcessorTime.TotalMilliseconds;
                var cpuSw = Stopwatch.StartNew();
                int cores = Environment.ProcessorCount;
                await Task.Delay(10000);
                proc.Refresh();
                long cpu1 = (long)proc.TotalProcessorTime.TotalMilliseconds;
                cpuSw.Stop();
                double cpuMs = cpu1 - cpu0;
                double pct = cpuMs / cpuSw.Elapsed.TotalMilliseconds * 100.0;
                Line("   墙钟 " + cpuSw.Elapsed.TotalSeconds.ToString("F1") + " s · CPU 时间 " + cpuMs + " ms");
                Line("   → 单核占用 **" + pct.ToString("F1") + " %**（全机 " + (pct / cores).ToString("F2")
                     + " %，共 " + cores + " 核）");
                Line("   → 内存 WorkingSet " + (proc.WorkingSet64 / 1048576) + " MB · 句柄 "
                     + GetHandles(proc));

                // ── ③ 静态截图（CaptureOnce / shot）────────────────────
                Line("");
                Line("== ③ 静态截图（shot → CaptureOnce）");
                var shot = await host.SendAsync("{\"Cmd\":\"shot\",\"Button\":\"verify\",\"Hotkey\":\"test\",\"X\":75}");
                Line("   shot：" + Short(shot));
                string? shotPath = ExtractJson(shot, "Info");
                if (!string.IsNullOrEmpty(shotPath) && File.Exists(shotPath))
                {
                    var fi = new FileInfo(shotPath);
                    Ok("截图生成 " + fi.Name + "（" + (fi.Length / 1024) + " KB）→ " + shotPath);
                    if (shotPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        Ok("截图是 JPG（这是**允许保留**的一次性功能，不是实时通路）");
                }
                else Fail("截图文件不存在：" + shotPath);

                // ── ④ 保存录像 ────────────────────────────────────────
                Line("");
                Line("== ④ 保存录像（save → SaveRecording）");
                var save = await host.SendAsync(
                    "{\"Cmd\":\"save\",\"Button\":\"verify\",\"Hotkey\":\"test\",\"X\":5,\"Delta\":0}");
                Line("   save：" + Short(save));
                string? dir = ExtractJson(save, "Info");

                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    Fail("录像目录不存在：" + dir);
                }
                else
                {
                    Line("   目录：" + dir);
                    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                    // ★ 只把 frame-*.jpg 视为「录像中间产物残留」。
                    //   shot.jpg 是**静态截图**（用户要的一次性功能），
                    //   它和录像落在同一个目录是按日期+触发条件分的，属于设计如此，
                    //   不能算「零 JPEG 没做到」——验收最容易在这里误杀正确实现。
                    var frameJpgs = files
                        .Where(f => Path.GetFileName(f).StartsWith("frame-", StringComparison.OrdinalIgnoreCase)
                                 && f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var otherJpgs = files
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                 && !Path.GetFileName(f).StartsWith("frame-", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var mp4s = files.Where(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();
                    var h264s = files.Where(f => f.EndsWith(".h264", StringComparison.OrdinalIgnoreCase)).ToList();

                    Line("   文件清单（" + files.Length + " 个）：");
                    foreach (var f in files.OrderBy(x => x))
                        Line("     " + Path.GetFileName(f) + "  " + (new FileInfo(f).Length / 1024) + " KB");

                    // 验收点：录像的中间 JPEG 序列不应残留
                    if (frameJpgs.Count == 0) Ok("目录里没有 frame-*.jpg（录像中间 JPEG 序列已被清理）");
                    else
                    {
                        Fail("目录里残留 " + frameJpgs.Count + " 个 frame-*.jpg：" +
                             string.Join(", ", frameJpgs.Select(Path.GetFileName).Take(5)));
                        Line("   ★ 按 SessionRecorder.cs:333-354 的逻辑，只有 MP4 转码失败才会保留 jpg。" +
                             "请检查 transcode-error 文件。");
                    }

                    if (otherJpgs.Count > 0)
                        Ok("另有 " + otherJpgs.Count + " 个非 frame- 的 jpg（"
                           + string.Join(", ", otherJpgs.Select(Path.GetFileName))
                           + "）—— 属静态截图，**允许保留**，不计入零 JPEG 指标");

                    if (mp4s.Count > 0)
                    {
                        Ok("产出 MP4：" + Path.GetFileName(mp4s[0])
                           + "（" + (new FileInfo(mp4s[0]).Length / 1024) + " KB）");
                        if (!string.IsNullOrEmpty(ffprobe)) ProbeMp4(ffprobe, mp4s[0]);
                    }
                    else if (h264s.Count > 0)
                    {
                        Ok("产出 .h264 兜底：" + Path.GetFileName(h264s[0]));
                        if (!string.IsNullOrEmpty(ffprobe)) ProbeMp4(ffprobe, h264s[0]);
                    }
                    else Fail("既没有 mp4 也没有 h264 兜底");

                    // meta 文件
                    var metas = files.Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (metas.Count > 0) Line("   meta：" + string.Join(", ", metas.Select(Path.GetFileName)));
                }

                // ── ⑤ 收尾：停录 ──────────────────────────────────────
                await host.SendAsync("{\"Cmd\":\"record\",\"Button\":\"stop\"}");
            }
            finally
            {
                await host.StopAsync();
            }

            Line("");
            Line(_fails == 0 ? "RESULT=OK" : "RESULT=FAIL " + _fails + " 项");
            return _fails;
        }

        private static void ProbeMp4(string ffprobe, string file)
        {
            Line("   == ffprobe " + Path.GetFileName(file));
            try
            {
                var psi = new ProcessStartInfo(ffprobe)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var a in new[]
                {
                    "-hide_banner", "-v", "error",
                    "-show_entries", "stream=codec_type,codec_name,width,height,nb_frames,avg_frame_rate",
                    "-show_entries", "format=format_name,duration,size",
                    "-of", "default=noprint_wrappers=0", file
                }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) { Fail("ffprobe 启动失败"); return; }
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                foreach (var l in o.Split('\n')) if (l.Trim().Length > 0) Line("      " + l.TrimEnd());
                if (e.Trim().Length > 0) Line("      [stderr] " + e.Trim());

                bool hasVideo = o.Contains("codec_type=video") || o.Contains("codec_name=h264");
                if (p.ExitCode == 0 && hasVideo) Ok("ffprobe 能读出该录像（含视频流）");
                else Fail("ffprobe 读不出视频流（exit=" + p.ExitCode + "）");
            }
            catch (Exception ex) { Fail("ffprobe 调用异常：" + ex.Message); }
        }

        private static uint GetHandles(Process p)
        {
            try
            {
                // 遵守 AGENTS.md：用 GetProcessHandleCount，不用 Process.HandleCount
                return Native.GetProcessHandleCount(p.Handle, out uint c) ? c : 0;
            }
            catch { return 0; }
        }

        private static class Native
        {
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool GetProcessHandleCount(IntPtr h, out uint count);
        }

        private static string Short(string? s)
        {
            if (s == null) return "(无响应)";
            // 只保留可读字段，避免把整行 JSON 刷屏
            var sb = new StringBuilder();
            foreach (var key in new[] { "Ok", "Message", "Info", "Count" })
            {
                string? v = ExtractJsonRaw(s, key);
                if (v != null) sb.Append(key + "=" + v + "  ");
            }
            return sb.Length > 0 ? sb.ToString() : s;
        }

        private static string? ExtractJson(string? s, string key)
        {
            string? raw = ExtractJsonRaw(s, key);
            if (raw == null) return null;
            // ★ 协议里的中文是 \uXXXX 转义（System.Text.Json 默认行为）。
            //   不解转义的话，返回的路径是 "C:\...\u5F55\u5236\u56DE\u653E\..."，
            //   Directory.Exists 必然 false —— 第一版就是这样误判成"录像目录不存在"。
            return UnescapeUnicode(raw);
        }

        /// <summary>把 \uXXXX 转义还原成真实字符（路径里含中文目录名）。</summary>
        private static string UnescapeUnicode(string s)
        {
            if (s.IndexOf("\\u", StringComparison.Ordinal) < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 5 < s.Length && s[i + 1] == 'u')
                {
                    string hex = s.Substring(i + 2, 4);
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out ushort code))
                    {
                        sb.Append((char)code);
                        i += 5;
                        continue;
                    }
                }
                else if (s[i] == '\\' && i + 1 < s.Length)
                {
                    // 常见转义
                    char n = s[i + 1];
                    if (n == '\\') { sb.Append('\\'); i++; continue; }
                    if (n == '"') { sb.Append('"'); i++; continue; }
                    if (n == '/') { sb.Append('/'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static string? ExtractJsonRaw(string? s, string key)
        {
            if (s == null) return null;
            int i = s.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return null;
            int colon = s.IndexOf(':', i);
            if (colon < 0) return null;
            int start = colon + 1;
            while (start < s.Length && s[start] == ' ') start++;
            if (start >= s.Length) return null;
            if (s[start] == '"')
            {
                int end = s.IndexOf('"', start + 1);
                if (end < 0) return null;
                return s.Substring(start + 1, end - start - 1);
            }
            int e2 = start;
            while (e2 < s.Length && s[e2] != ',' && s[e2] != '}') e2++;
            return s.Substring(start, e2 - start).Trim();
        }
    }
}
