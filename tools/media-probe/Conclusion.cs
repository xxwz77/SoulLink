using System;
using System.Collections.Generic;

namespace MediaProbe
{
    /// <summary>
    /// 把探测结果翻译成「60fps 到底可不可行、走哪条路」的结论。
    ///
    /// ★ 为什么要单独一个类：
    ///   原始探测只会输出一堆 hr 和 ms，读者无法直接决策。
    ///   这一段把「采集耗时 + 编码耗时」相加，和 16.7ms（60fps 预算）/ 33.3ms（30fps 预算）
    ///   对比，给出明确的「可行 / 不可行 / 有条件可行」。
    /// </summary>
    internal static class Conclusion
    {
        public static List<string> Build(
            Program.DupProbe dup, Program.DupStats stats,
            double bitBltAvg, double jpegAvg,
            MfInfo mf, bool isRemote, bool d3dOk)
        {
            var lines = new List<string>();

            // ── 前提声明：会话性质决定结论的适用范围 ────────────────
            lines.Add(isRemote
                ? "■ 会话性质：**RDP 会话** —— 下列结论直接适用于「代理跑在 RDP 会话里」的目标场景。"
                : "■ 会话性质：**本地控制台（非 RDP）** —— 下列结论只代表物理桌面，"
                  + "**不能**直接套用到 RDP 会话；RDP 下的关键项必须复测。");
            lines.Add("");

            // ── 桌面复制可用性 ──────────────────────────────────────
            if (!d3dOk)
            {
                lines.Add("■ 桌面复制：**不可用** —— D3D11 设备都建不起来，这条路径在本环境直接出局。");
            }
            else if (dup == null)
            {
                lines.Add("■ 桌面复制：**不可用** —— 有 D3D 设备但 DuplicateOutput 在所有输出上都失败。");
                lines.Add("   → 结论：不能把 (e) DXGI 方案当作 P2 的既定路径，必须先解决可用性。");
            }
            else
            {
                lines.Add($"■ 桌面复制：**可用**（输出 {dup.OutputIndex} {dup.DeviceName}）。");
            }

            double captureMs = -1;
            if (dup != null && stats != null && stats.AcquireMs.Count > 0)
            {
                double sum = 0; foreach (var v in stats.AcquireMs) sum += v;
                captureMs = sum / stats.AcquireMs.Count;
            }

            // ── 采集耗时对比 ────────────────────────────────────────
            lines.Add("");
            lines.Add("■ 采集耗时（单帧，仅采集不含编码）：");
            if (captureMs > 0)
                lines.Add($"   · 桌面复制 : {captureMs:F3} ms  → 理论上限 {1000.0 / captureMs:F0} fps");
            else
                lines.Add("   · 桌面复制 : 无法测量（不可用）");
            if (bitBltAvg > 0)
                lines.Add($"   · BitBlt   : {bitBltAvg:F3} ms  → 理论上限 {1000.0 / bitBltAvg:F0} fps");
            else
                lines.Add("   · BitBlt   : 无法测量");

            if (captureMs > 0 && bitBltAvg > 0)
            {
                double gain = bitBltAvg / captureMs;
                lines.Add($"   → 桌面复制比 BitBlt {(gain >= 1 ? "快" : "慢")} {Math.Abs(gain):F1}×");
            }
            lines.Add("   ★ 注意：BitBlt 只是**抓屏**，现在线上还有 Image.FromHbitmap（GDI→GDI+ 又拷一次）"
                    + "和 GDI+ JPEG 软编；桌面复制省掉的是前两段。");
            lines.Add("");

            // ── 编码耗时对比 ────────────────────────────────────────
            lines.Add("■ 编码耗时（单帧 1080p）：");
            if (jpegAvg > 0)
                lines.Add($"   · GDI+ JPEG q75 : {jpegAvg:F3} ms → 上限 {1000.0 / jpegAvg:F0} fps");
            else
                lines.Add("   · GDI+ JPEG q75 : 无法测量");
            bool h264Usable = mf != null && mf.H264EncodeMsAvg > 0 && mf.H264FileBytesTotal >= 300 * 1024;
            if (h264Usable)
                lines.Add($"   · MF H.264      : {mf.H264EncodeMsAvg:F3} ms（**CPU 侧提交成本下界**，"
                        + $"异步 MFT 在 GPU 上完成实际编码）→ CPU 视角上限 {1000.0 / mf.H264EncodeMsAvg:F0} fps");
            else if (mf != null && mf.H264EncodeMsAvg > 0)
                lines.Add($"   · MF H.264      : **未取得可信数字**（测得 {mf.H264EncodeMsAvg:F3}ms，"
                        + $"但 40 帧仅 {mf.H264FileBytesTotal / 1024}KB，未形成有效码流）");
            else
                lines.Add($"   · MF H.264      : 未测得（{mf?.EncodeNote ?? "MF 不可用"}）");

            if (mf != null)
            {
                lines.Add($"   · 硬件 MFT      : {(mf.HardwareEncoders.Count > 0 ? mf.HardwareEncoders.Count + " 个" : "无")}");
                lines.Add($"   · 软件 MFT      : {mf.SoftwareEncoders.Count} 个");
            }
            lines.Add("");

            // ── 60fps 判定 ──────────────────────────────────────────
            lines.Add("■ 60fps（16.7ms/帧预算）判定：");
            double nowTotal = (bitBltAvg > 0 && jpegAvg > 0) ? bitBltAvg + jpegAvg : -1;
            if (nowTotal > 0)
                lines.Add($"   · 现有路径 BitBlt+JPEG 合计 ≈ {nowTotal:F2} ms/帧 → "
                        + $"{(nowTotal <= 16.7 ? "**够 60fps**" : $"只能约 {1000.0 / nowTotal:F0} fps")}");

            if (captureMs > 0 && jpegAvg > 0)
            {
                double dd = captureMs + jpegAvg;
                lines.Add($"   · 桌面复制 + JPEG   合计 ≈ {dd:F2} ms/帧 → "
                        + $"{(dd <= 16.7 ? "**够 60fps**" : $"只能约 {1000.0 / dd:F0} fps")}");
            }
            bool h264Ok = mf != null && mf.H264EncodeMsAvg > 0 && mf.H264FileBytesTotal >= 300 * 1024;
            if (captureMs > 0 && h264Ok)
            {
                double dh = captureMs + mf.H264EncodeMsAvg;
                lines.Add($"   · 桌面复制 + H.264  合计 ≈ {dh:F2} ms/帧（CPU 下界） → "
                        + $"{(dh <= 16.7 ? "**CPU 侧够 60fps**" : $"只能约 {1000.0 / dh:F0} fps")}");
                lines.Add("     ★ 这个合计是**下界**：真正能不能 60fps 还取决于 GPU 编码吞吐、");
                lines.Add("       NVENC 并发会话数限制、以及 GDI→GPU 上传带宽，本程序都没测。");
            }
            else if (captureMs > 0)
            {
                lines.Add("   · 桌面复制 + H.264  ：**无法判定** —— H.264 编码耗时未取得可信数字，"
                        + "需要先修好编码探测（见 §7 诊断）");
            }

            lines.Add("");
            // ── 硬件编码的真实约束（必须说，否则会被误读成「有硬编就万事大吉」）──
            lines.Add("■ 硬件编码的已知约束（决定它能不能真的用上）：");
            lines.Add("   · NVENC 在消费级驱动上对**并发会话数**有限制（历史上 2~3 路，新版 5~8 路）；");
            lines.Add("     8 个账号同时录制是真实场景，这个限制必须先验证再依赖。");
            lines.Add("   · 硬编要求输入是 GPU 纹理；当前采集走 BitBlt 的 GDI 位图，");
            lines.Add("     要先上传到 GPU —— 这段拷贝本身有成本，本程序未测。");
            lines.Add("   · 本程序测的是「系统内存 NV12 → MFT」路径，不是 GPU 零拷贝直通。");
            lines.Add("     注意方向：这条路径**多了一次系统内存→GPU 上传**，从这一点看是偏悲观；");
            lines.Add("     但 WriteSample 是异步入队、CPU 侧只看到提交成本，从这一点看又**偏乐观**。");
            lines.Add("     两个偏差方向相反，所以该数字只能当「量级参考」，不能当精确 CPU 预算。");

            return lines;
        }
    }
}
