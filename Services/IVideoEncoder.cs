using System;

namespace SoulLink.Services
{
    /// <summary>
    /// 视频编码器抽象（H.264 第一步）。
    ///
    /// ★ 为什么抽这个接口：
    ///   抓屏抽象（Relay/IScreenCapture.cs）解决的是「帧从哪来」，这个接口解决
    ///   「帧往哪编码」。两者正交：WGC/BitBlt 抓出来的帧都要编码，但编码器
    ///   （NVENC 硬编 / MF 软编）是可替换的。
    ///
    /// ★ 两种输入（给 GPU 零拷贝路径预留）：
    ///   · CPU BGRA（第一步实现）：读回 CPU 的像素，软件编码；
    ///   · D3D11 纹理（第二步预留）：GPU 直通，NVENC 直接吃，零读回。
    ///
    /// ★ 第一步（MfH264Encoder）只做 CPU 输入，跑通「能编码出可播放的 H.264」；
    ///   GPU 纹理路径是第二步，接口已留位置，不改签名。
    /// </summary>
    public interface IVideoEncoder : IDisposable
    {
        /// <summary>编码器名（"MF-H264" / "NVENC"），供日志与状态回显。</summary>
        string Name { get; }

        /// <summary>
        /// 初始化编码器。必须在 Encode 之前调用一次。
        /// </summary>
        bool TryInitialize(int width, int height, int fps, int bitrateKbps);

        /// <summary>
        /// 编码一帧 CPU BGRA 像素。
        ///
        /// ★ bgra：BGRA 布局、**自下而上**（bottom-up，与 MF RGB32 一致，
        ///   见 Mp4Writer.LoadAsBgraBottomUp 的约定）。
        /// ★ stride：每行字节数（紧凑布局 = width*4）。
        /// ★ nalUnits：帧级编码输出。第一步（SinkWriter）不逐帧暴露 NAL，
        ///   恒为 null；完整可播放流在 <see cref="Complete"/> 里一次性产出。
        /// </summary>
        bool Encode(byte[] bgra, int stride, long timestampUs, out byte[]? nalUnits);

        /// <summary>
        /// 编码一帧 GPU 纹理（第二步预留）。第一步抛 NotSupportedException。
        /// </summary>
        bool Encode(IntPtr d3dTexture, long timestampUs, out byte[]? nalUnits);

        /// <summary>请求下一帧为关键帧（第一步 SinkWriter 自动管理，返回 false）。</summary>
        bool RequestKeyFrame();

        /// <summary>
        /// 结束编码流，产出完整可播放的容器字节（第一步 = MP4）。
        /// 调用后编码器不可再 Encode。
        /// </summary>
        bool Complete(out byte[] containerBytes);
    }
}
