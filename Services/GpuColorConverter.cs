using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Services
{
    /// <summary>
    /// GPU 色彩转换器：B8G8R8A8 纹理 → NV12 纹理（ID3D11VideoProcessor，一步 VideoProcessorBlt）。
    ///
    /// ★ 为什么用 VideoProcessor 而非着色器：NV12 是双平面（Y 平面 + UV 交错半分辨率），
    ///   不能作为单一 RTV 直接渲染；着色器方案要两 pass + 合成（复杂 3 倍）。
    ///   VideoProcessorBlt 原生支持 BGRA→NV12 一步完成，是官方为该场景设计的标准 API。
    ///
    /// ★ 设备共享（保守版的关键）：本类复用 NvencSdkEncoder 已创建的 ID3D11Device，
    ///   使产出的 NV12 纹理与 NVENC 会话在同一 device 上，可直接 RegisterResource/Map，
    ///   不引入跨设备注册的额外复杂度。WGC 的 BGRA 纹理通过 NTHandle 共享句柄跨设备打开。
    ///
    /// ★ 静默降级：任何一步失败都返回 null（IsAvailable=false），上层回落到 CPU 路径，
    ///   绝不抛异常进入崩溃路径（RDP 会话内 ID3D11VideoDevice 不可用是已知风险）。
    /// </summary>
    internal sealed class GpuColorConverter : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private ID3D11VideoDevice? _videoDevice;
        private ID3D11VideoContext? _videoContext;
        private ID3D11VideoProcessorEnumerator? _enumerator;
        private ID3D11VideoProcessor? _processor;
        private ID3D11VideoProcessorInputView? _inputView;
        private ID3D11VideoProcessorOutputView? _outputView;
        private ID3D11Texture2D? _nv12Texture;
        private int _width, _height;
        private bool _disposed;

        public bool IsAvailable => _processor != null && _inputView != null && _outputView != null && _nv12Texture != null;
        public string LastError { get; private set; } = "";

        public GpuColorConverter(ID3D11Device device, ID3D11DeviceContext context)
        {
            _device = device;
            _context = context;
        }

        /// <summary>
        /// 初始化 VideoProcessor（输入 B8G8R8A8，输出 NV12）。尺寸变化时惰性重建。
        /// 返回 false 表示不可用，调用方降级 CPU 路径。
        /// </summary>
        public bool TryInitialize(int width, int height)
        {
            if (_disposed) return false;
            if (IsAvailable && _width == width && _height == height) return true;
            LastError = "";

            // NV12 要求偶数对齐
            int w = width & ~1, h = height & ~1;
            if (w <= 0 || h <= 0) { LastError = "尺寸无效"; return false; }

            try
            {
                // 释放旧资源
                ReleaseViews();

                // 从 ID3D11Device 取 ID3D11VideoDevice
                _videoDevice = _device.QueryInterface<ID3D11VideoDevice>();
                if (_videoDevice == null) { LastError = "QueryInterface ID3D11VideoDevice 失败"; return false; }
                _videoContext = _context.QueryInterface<ID3D11VideoContext>();
                if (_videoContext == null) { LastError = "QueryInterface ID3D11VideoContext 失败"; return false; }

                // 枚举 VideoProcessor（输入 B8G8R8A8，输出 NV12）
                var desc = new VideoProcessorContentDescription();
                desc.InputFrameFormat = VideoFrameFormat.Progressive;
                desc.InputWidth = (uint)w;
                desc.InputHeight = (uint)h;
                desc.OutputWidth = (uint)w;
                desc.OutputHeight = (uint)h;
                desc.Usage = VideoUsage.PlaybackNormal;
                // 帧率：30fps（与直播一致，VideoProcessor 的帧率转换不启用时此项影响小）
                desc.InputFrameRate = new Vortice.DXGI.Rational(30u, 1u);
                desc.OutputFrameRate = new Vortice.DXGI.Rational(30u, 1u);

                _videoDevice.CreateVideoProcessorEnumerator(ref desc, out var enumerator);
                _enumerator = enumerator;
                if (_enumerator == null) { LastError = "CreateVideoProcessorEnumerator 失败"; return false; }

                _videoDevice.CreateVideoProcessor(_enumerator, 0, out var processor);
                _processor = processor;
                if (_processor == null) { LastError = "CreateVideoProcessor 失败"; return false; }

                // 创建 NV12 输出纹理（RenderTarget 用途，供 VideoProcessor 写）
                _nv12Texture = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                });
                if (_nv12Texture == null) { LastError = "创建 NV12 纹理失败"; return false; }

                // 输出视图
                var ovd = new VideoProcessorOutputViewDescription();
                ovd.ViewDimension = VideoProcessorOutputViewDimension.Texture2D;
                ovd.Texture2D.MipSlice = 0;
                _videoDevice.CreateVideoProcessorOutputView(_nv12Texture, _enumerator, ovd, out var outputView);
                _outputView = outputView;
                if (_outputView == null) { LastError = "CreateVideoProcessorOutputView 失败"; return false; }

                _width = w; _height = h;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                ReleaseViews();
                return false;
            }
        }

        /// <summary>
        /// 把一块 B8G8R8A8 纹理（同 device）转成 NV12，写入 _nv12Texture。
        /// 输入纹理必须与 _device 同 device（调用方保证，跨设备由调用方先 CopyResource）。
        /// 返回 false = 转换失败（调用方降级）。
        /// </summary>
        public bool Convert(ID3D11Texture2D bgraTexture, out ID3D11Texture2D nv12Texture)
        {
            nv12Texture = null!;
            if (_disposed || !IsAvailable || _videoDevice == null || _enumerator == null || _processor == null || _nv12Texture == null)
                return false;

            try
            {
                // 输入视图（每帧创建一次视图成本低；也可缓存，但尺寸/纹理可能变化，保守每帧建）
                var ivd = new VideoProcessorInputViewDescription();
                ivd.FourCC = 0;   // 0 = 用资源的 DXGI 格式
                ivd.ViewDimension = VideoProcessorInputViewDimension.Texture2D;
                ivd.Texture2D.MipSlice = 0;
                ivd.Texture2D.ArraySlice = 0;
                _videoDevice.CreateVideoProcessorInputView(bgraTexture, _enumerator, ivd, out var inputView);
                if (inputView == null) return false;
                try
                {
                    var stream = new VideoProcessorStream();
                    stream.Enable = true;
                    stream.OutputIndex = 0;
                    stream.InputFrameOrField = 0;
                    stream.PastFrames = 0;
                    stream.FutureFrames = 0;
                    stream.InputSurface = inputView;
                    stream.PpPastSurfaces = null!;
                    stream.PpFutureSurfaces = null!;
                    stream.InputSurfaceRight = null!;
                    stream.PpPastSurfacesRight = null!;
                    stream.PpFutureSurfacesRight = null!;

                    var streams = new[] { stream };
                    _videoContext!.VideoProcessorBlt(_processor, _outputView!, 0, (uint)streams.Length, streams);
                    nv12Texture = _nv12Texture;
                    return true;
                }
                finally { inputView.Dispose(); }
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 零拷贝入口：从 WGC 的跨设备共享句柄打开 B8G8R8A8 纹理，CopyResource 到本设备 BGRA staging，
        /// 再 VideoProcessorBlt 转 NV12（写入本设备的 _nv12Texture）。全程显存内，不落 CPU。
        /// 任一步失败返回 false（调用方降级 CPU 路径）。
        /// </summary>
        public bool ConvertFromSharedHandle(IntPtr sharedHandle, int width, int height, out ID3D11Texture2D nv12Texture)
        {
            nv12Texture = null!;
            if (_disposed || sharedHandle == IntPtr.Zero || width <= 0 || height <= 0) return false;
            if (!IsAvailable) return false;

            int w = width & ~1, h = height & ~1;
            if (w <= 0 || h <= 0) return false;
            if (w != _width || h != _height) { if (!TryInitialize(w, h)) return false; }

            try
            {
                // 跨设备打开共享纹理（同进程不同 ID3D11Device 之间）
                using var sharedTex = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                if (sharedTex == null) return false;

                // 拷贝到本设备 BGRA staging（显存内 CopyResource，无 CPU 往返）
                using var localBgra = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                });
                if (localBgra == null) return false;
                _context.CopyResource(localBgra, sharedTex);

                return Convert(localBgra, out nv12Texture);
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        private void ReleaseViews()
        {
            try { _outputView?.Dispose(); } catch { }
            try { _inputView?.Dispose(); } catch { }
            try { _processor?.Dispose(); } catch { }
            try { _enumerator?.Dispose(); } catch { }
            try { _nv12Texture?.Dispose(); } catch { }
            try { _videoDevice?.Dispose(); } catch { }
            try { _videoContext?.Dispose(); } catch { }
            _outputView = null; _inputView = null; _processor = null; _enumerator = null;
            _nv12Texture = null; _videoDevice = null; _videoContext = null;
            _width = _height = 0;
        }

        /// <summary>
        /// 读回 NV12 纹理到 CPU（仅诊断用，不进入热路径）。返回 null = 读回失败。
        /// </summary>
        public byte[]? ReadBackNv12(ID3D11Texture2D nv12Tex, int width, int height)
        {
            if (_disposed || nv12Tex == null || width <= 0 || height <= 0) return null;
            try
            {
                var staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = Format.NV12,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                });
                using (staging)
                {
                    _context.CopyResource(staging, nv12Tex);
                    var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int ySize = width * height;
                        int total = ySize + ySize / 2;
                        var nv12 = new byte[total];
                        int rowPitch = (int)mapped.RowPitch;
                        // Y 平面：逐行拷贝（rowPitch 可能 > width 对齐）
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * rowPitch), nv12, y * width, width);
                        // UV 平面：从 Y 平面之后开始（height 行之后），半分辨率交错
                        IntPtr uvBase = IntPtr.Add(mapped.DataPointer, rowPitch * height);
                        int uvRowPitch = rowPitch;   // NV12 UV 平面 rowPitch 通常与 Y 相同
                        for (int y = 0; y < height / 2; y++)
                            Marshal.Copy(IntPtr.Add(uvBase, y * uvRowPitch), nv12, ySize + y * width, width);
                        return nv12;
                    }
                    finally { _context.Unmap(staging, 0); }
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// 计算两块 NV12 的 PSNR（对 Y 平面）。返回 double.MaxValue 表示完全相同或输入无效。
        /// 诊断用：零拷贝 GPU 转出的 NV12 vs CPU BgraToNv12 的 NV12 同帧对比。
        /// </summary>
        public static double ComputePsnrY(byte[] a, byte[] b, int width, int height)
        {
            if (a == null || b == null || width <= 0 || height <= 0) return double.MaxValue;
            int ySize = width * height;
            if (a.Length < ySize || b.Length < ySize) return double.MaxValue;
            double mse = 0;
            for (int i = 0; i < ySize; i++)
            {
                double d = a[i] - b[i];
                mse += d * d;
            }
            mse /= ySize;
            if (mse < 1e-9) return double.MaxValue;
            return 10.0 * Math.Log10(255.0 * 255.0 / mse);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseViews();
        }
    }
}
