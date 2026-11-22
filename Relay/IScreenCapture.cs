using System;

namespace SoulLink.Relay
{
    /// <summary>
    /// 抓屏后端抽象。
    ///
    /// ★ 为什么抽这个接口（2026-11-22）：
    ///   现有 SessionRecorder 把「抓屏（BitBlt）+ 光标叠加 + JPEG 编码」焊死在一个
    ///   GrabJpeg 方法里。要换成 WGC（Windows.Graphics.Capture）抓屏、乃至后续换
    ///   H.264 硬编，必须先有个缝把「抓屏后端」与「录制/直播调度」分开。
    ///
    /// ★ 第一步（本文件）只有一个实现 BitBltCapture，行为与旧 GrabJpeg 完全一致；
    ///   WGC 是第二步，实现同一个接口即可接入（见任务报告「第二步怎么接」）。
    /// </summary>
    internal interface IScreenCapture
    {
        /// <summary>后端名（"BitBlt" / "WGC"），供 recordstatus 与日志回显，便于排障。</summary>
        string Name { get; }

        /// <summary>
        /// 抓当前会话的一帧并编码为 JPEG。
        ///
        /// ★ 返回 null 表示本帧抓取/编码失败。调用方照旧跳过该帧（行为与旧 GrabJpeg 一致：
        ///   旧实现 catch 一切异常后返回 null，由录制循环判断跳过）。
        /// ★ width/height 是本次实际抓取的像素尺寸；失败时为 0。
        /// </summary>
        byte[]? CaptureJpeg(int quality, out int width, out int height);
    }
}
