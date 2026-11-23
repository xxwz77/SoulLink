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
    internal interface IScreenCapture : IDisposable
    {
        /// <summary>后端名（"BitBlt" / "WGC"），供 recordstatus 与日志回显，便于排障。</summary>
        string Name { get; }

        /// <summary>桌面真实出帧累计序号（静止时不递增）。BitBlt 无此语义，恒返回 0。</summary>
        long FrameSequence { get; }

        /// <summary>
        /// 抓当前会话的一帧并编码为 JPEG。
        ///
        /// ★ 返回 null 表示本帧抓取/编码失败。调用方照旧跳过该帧（行为与旧 GrabJpeg 一致：
        ///   旧实现 catch 一切异常后返回 null，由录制循环判断跳过）。
        /// ★ width/height 是本次实际抓取的像素尺寸；失败时为 0。
        /// </summary>
        byte[]? CaptureJpeg(int quality, out int width, out int height);

        /// <summary>抓一帧原始 BGRA（top-down，紧凑 stride=width*4）。实时链路禁止 JPEG 中转。</summary>
        byte[]? CaptureBgra(out int width, out int height);

        /// <summary>
        /// 零拷贝（GPU 阶段B）：返回一帧 BGRA 的**跨设备共享句柄**（纹理已留在显存，不读回 CPU）。
        /// 仅 GPU 抓屏后端（WGC/DDA）支持；BitBlt 是 CPU 抓屏，恒返回 false。
        /// 返回 false 表示「本帧无纹理可直通」（静止无帧 / 共享句柄拿不到 / 后端不支持），
        /// 调用方必须回退 CPU 路径（CaptureBgra + BgraToNv12），绝不空转。
        /// </summary>
        bool TryCaptureBgraTexture(out IntPtr sharedHandle, out int width, out int height);
    }
}
