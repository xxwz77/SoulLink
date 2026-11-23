using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Relay
{
    /// <summary>
    /// ★ P0-F（2026-11-24）：GPU 色彩转换器——像素着色器两 pass BGRA→NV12，对齐 Sunshine
    ///   display_vram.cpp convert()（PS draw Y → NV12 plane0 的 R8_UNorm RTV；PS draw UV →
    ///   同纹理 plane1 的 R8G8_UNorm RTV；UV viewport 半宽半高）。
    ///
    /// ★ 为什么不用 VideoProcessor：ID3D11VideoDevice/VideoProcessorBlt 在 RDP 会话内不可用
    ///   （真机实测「零拷贝连续失败 3 次」即此路径）。着色器路线只依赖 D3D11 硬件设备 + d3dcompiler，
    ///   RDP 内可用（与 Sunshine 同构）。
    ///
    /// ★ 输出纹理 = NvencSdkEncoder 注册的 Nv12InputTexture（外部传入，本类不持有生命周期）：
    ///   EncodeD3D11Texture 要求 ReferenceEquals(nv12Tex, 编码器注册纹理)（NvencSdkEncoder.cs:721），
    ///   所以必须直接渲染进那张纹理，而不是自建一张再拷贝。
    ///
    /// ★ 色彩系数与 CPU BgraToNv12（SessionRecorder.cs）完全一致（BT.601 full-range）：
    ///   Y  = ( 66r + 129g +  25b)/256 + 0.5
    ///   U  = (-38r -  74g + 112b)/256 + 0.5 + 128/255（2x2 平均后再套公式）
    ///   V  = (112r -  94g -  18b)/256 + 0.5 + 128/255
    ///   保证零拷贝 PSNR 对比（GPU vs CPU 同帧 NV12）≥40dB。
    ///
    /// ★ 静默降级：任何失败返回 false（LastError 记录），上层回落 CPU 路径，绝不抛异常。
    /// </summary>
    internal sealed class GpuShaderConverter : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Texture2D _nv12Texture;   // 编码器注册的 NV12 输入纹理（外部生命周期）

        private ID3D11Texture2D? _localBgra;             // 编码设备上的 BGRA 副本（SRV 源）
        private ID3D11ShaderResourceView? _bgraSrv;
        private ID3D11RenderTargetView? _rtvY;           // NV12 plane 0（R8_UNorm 视图）
        private ID3D11RenderTargetView? _rtvUv;          // NV12 plane 1（R8G8_UNorm 视图）
        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psY, _psUv;
        private ID3D11SamplerState? _sampler;
        private ID3D11RasterizerState? _raster;        // ★ P0-F：CullMode.None（双保险，防绕序问题）
        private int _width, _height;
        private bool _disposed;

        public bool IsAvailable => _vs != null && _psY != null && _psUv != null
            && _rtvY != null && _rtvUv != null && _localBgra != null && _bgraSrv != null;
        public string LastError { get; private set; } = "";

        public GpuShaderConverter(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D encoderNv12Texture)
        {
            _device = device;
            _context = context;
            _nv12Texture = encoderNv12Texture;
        }

        public bool TryInitialize(int width, int height)
        {
            if (_disposed) return false;
            if (IsAvailable && _width == width && _height == height) return true;
            LastError = "";

            int w = width & ~1, h = height & ~1;   // NV12 偶数对齐
            if (w <= 0 || h <= 0) { LastError = "尺寸无效"; return false; }

            try
            {
                ReleaseGpuResources();

                // ── 着色器编译（d3dcompiler_47.dll，Win10+ 系统自带）──
                var vsCode = CompileShader(VsHlsl, "VSMain", "vs_5_0");
                var psYCode = CompileShader(PsYHlsl, "PSMain", "ps_5_0");
                var psUvCode = CompileShader(PsUvHlsl, "PSMain", "ps_5_0");
                if (vsCode == null || psYCode == null || psUvCode == null) return false;

                _vs = _device.CreateVertexShader(vsCode, null);
                _psY = _device.CreatePixelShader(psYCode, null);
                _psUv = _device.CreatePixelShader(psUvCode, null);
                if (_vs == null || _psY == null || _psUv == null) { LastError = "创建着色器失败"; return false; }

                // ── 采样器：线性 + clamp（Y pass 像素中心采样，恰好无插值误差）──
                _sampler = _device.CreateSamplerState(new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunc = ComparisonFunction.Never,
                    MinLOD = 0, MaxLOD = 0
                });
                _raster = _device.CreateRasterizerState(new RasterizerDescription
                {
                    CullMode = CullMode.None,
                    FillMode = FillMode.Solid
                });

                // ── 本设备 BGRA 副本 + SRV（跨设备共享纹理先 CopyResource 到这里）──
                _localBgra = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.None
                });
                if (_localBgra == null) { LastError = "创建本地 BGRA 纹理失败"; return false; }

                _bgraSrv = _device.CreateShaderResourceView(_localBgra, new ShaderResourceViewDescription
                {
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Format = Format.B8G8R8A8_UNorm,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
                });
                if (_bgraSrv == null) { LastError = "创建 BGRA SRV 失败"; return false; }

                // ── NV12 双平面 RTV：格式化视图（D3D11 原生支持，无需 11.1 平面切片 API）──
                _rtvY = _device.CreateRenderTargetView(_nv12Texture,
                    new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8_UNorm, 0, 0, 1));
                _rtvUv = _device.CreateRenderTargetView(_nv12Texture,
                    new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8G8_UNorm, 0, 0, 1));
                if (_rtvY == null || _rtvUv == null) { LastError = "创建 NV12 平面 RTV 失败（R8/R8G8 视图）"; return false; }

                _width = w; _height = h;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                ReleaseGpuResources();
                return false;
            }
        }

        /// <summary>
        /// 零拷贝入口：从抓屏端共享句柄打开 B8G8R8A8 纹理 → CopyResource 到本设备 BGRA 纹理 →
        /// PS 两 pass 渲染进编码器注册的 NV12 纹理。全程显存内，不落 CPU。
        /// 返回的 nv12Texture 恒为编码器注册纹理（调用方直接 EncodeD3D11Texture）。
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
                // 跨设备打开共享纹理（同进程不同 ID3D11Device 之间；抓屏端已 Flush 保证可见）
                using var sharedTex = _device.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
                if (sharedTex == null) { LastError = "OpenSharedResource 失败"; return false; }
                _context.CopyResource(_localBgra!, sharedTex);

                // ── Y pass：full viewport，R8 RTV（NV12 plane 0）──
                _context.OMSetRenderTargets(_rtvY, null);
                _context.RSSetViewport(0, 0, w, h, 0, 1);
                _context.RSSetState(_raster);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.VSSetShader(_vs);
                _context.PSSetShader(_psY);
                _context.PSSetShaderResource(0, _bgraSrv);
                _context.PSSetSampler(0, _sampler);
                _context.Draw(3, 0);

                // ── UV pass：半分辨率 viewport，R8G8 RTV（NV12 plane 1）──
                _context.OMSetRenderTargets(_rtvUv, null);
                _context.RSSetViewport(0, 0, w / 2.0f, h / 2.0f, 0, 1);
                _context.PSSetShader(_psUv);
                _context.Draw(3, 0);

                // 解绑：同一纹理不能再同时作为 RTV/SRV（也保证 NVENC Map 时无活跃绑定）
                _context.PSSetShaderResource(0, null);
                _context.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);

                // NVENC MapInputResource 前 Flush，保证渲染结果对编码器可见
                _context.Flush();

                nv12Texture = _nv12Texture;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + "：" + ex.Message;
                return false;
            }
        }

        /// <summary>读回本设备 BGRA 副本到 CPU（仅诊断/PSNR 用——与 GPU NV12 同帧对比，不进热路径）。</summary>
        public byte[]? ReadBackBgra(int width, int height)
        {
            if (_disposed || _localBgra == null || width <= 0 || height <= 0) return null;
            try
            {
                var staging = _device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read,
                    MiscFlags = ResourceOptionFlags.None
                });
                using (staging)
                {
                    _context.CopyResource(staging, _localBgra);
                    var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        var tight = new byte[width * 4 * height];
                        int rowPitch = (int)mapped.RowPitch;
                        if (rowPitch == width * 4)
                            Marshal.Copy(mapped.DataPointer, tight, 0, tight.Length);
                        else
                            for (int y = 0; y < height; y++)
                                Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * rowPitch), tight, y * width * 4, width * 4);
                        return tight;
                    }
                    finally { _context.Unmap(staging, 0); }
                }
            }
            catch { return null; }
        }

        /// <summary>读回 NV12 到 CPU（仅诊断/PSNR 用，不进热路径）。</summary>
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
                        var nv12 = new byte[ySize + ySize / 2];
                        int rowPitch = (int)mapped.RowPitch;
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * rowPitch), nv12, y * width, width);
                        IntPtr uvBase = IntPtr.Add(mapped.DataPointer, rowPitch * height);
                        for (int y = 0; y < height / 2; y++)
                            Marshal.Copy(IntPtr.Add(uvBase, y * rowPitch), nv12, ySize + y * width, width);
                        return nv12;
                    }
                    finally { _context.Unmap(staging, 0); }
                }
            }
            catch { return null; }
        }

        private void ReleaseGpuResources()
        {
            try { _rtvY?.Dispose(); } catch { }
            try { _rtvUv?.Dispose(); } catch { }
            try { _bgraSrv?.Dispose(); } catch { }
            try { _localBgra?.Dispose(); } catch { }
            try { _sampler?.Dispose(); } catch { }
            try { _raster?.Dispose(); } catch { }
            try { _vs?.Dispose(); } catch { }
            try { _psY?.Dispose(); } catch { }
            try { _psUv?.Dispose(); } catch { }
            _rtvY = null; _rtvUv = null; _bgraSrv = null; _localBgra = null;
            _sampler = null; _raster = null; _vs = null; _psY = null; _psUv = null;
            _width = _height = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseGpuResources();
            // 不释放 _nv12Texture：属于编码器
        }

        // ── HLSL ────────────────────────────────────────────────────────────
        private const string VsHlsl = @"
// ★ 全屏三角形必须顺时针绕序（D3D 默认 CULL_BACK，逆时针会被整体剔除，画面全黑/全零——
//   真机 15dB PSNR 的根因之一）。顶点：(0,0)(2,0)(0,2) → 屏幕：左上、右上、左下。
void VSMain(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD0)
{
    float2 p = float2((id == 1) ? 2.0f : 0.0f, (id == 2) ? 2.0f : 0.0f);
    pos = float4(p.x * 2.0f - 1.0f, 1.0f - p.y * 2.0f, 0.0f, 1.0f);
    uv = float2(pos.x * 0.5f + 0.5f, 0.5f - pos.y * 0.5f);
}
";

        private const string PsYHlsl = @"
Texture2D tex : register(t0);
SamplerState samp : register(s0);
float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    float3 rgb = tex.Sample(samp, uv).rgb;
    // 与 CPU BgraToNv12 逐位对齐：y8=(66r+129g+25b+128)>>8，
    // 常数 128/256 在 [0,1] 空间 = 0.5/255 ≈ 0.001961（不是 +0.5！）
    float y = dot(rgb, float3(0.2578125f, 0.50390625f, 0.09765625f)) + 0.0019607843f;
    return float4(y, 0.0f, 0.0f, 1.0f);
}
";

        private const string PsUvHlsl = @"
Texture2D tex : register(t0);
float4 PSMain(float4 pos : SV_Position, float2 uv : TEXCOORD0) : SV_Target
{
    uint w, h;
    tex.GetDimensions(w, h);
    int2 t = (int2)(uv * float2((float)w, (float)h));
    t = t & ~1;
    float3 rgb = tex.Load(int3(t, 0)).rgb
               + tex.Load(int3(t + int2(1, 0), 0)).rgb
               + tex.Load(int3(t + int2(0, 1), 0)).rgb
               + tex.Load(int3(t + int2(1, 1), 0)).rgb;
    rgb *= 0.25f;
    float u = dot(rgb, float3(-0.1484375f, -0.2890625f, 0.4375f)) + 0.5039215686f;
    float v = dot(rgb, float3(0.4375f, -0.3671875f, -0.0703125f)) + 0.5039215686f;
    return float4(u, v, 0.0f, 1.0f);
}
";

        // ── D3DCompile（d3dcompiler_47.dll）──────────────────────────────────
        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int D3DCompile(
            [In] byte[] pSrcData, nuint srcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string pSourceName,
            IntPtr pDefines, IntPtr pInclude,
            [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            uint flags1, uint flags2, out IntPtr ppCode, out IntPtr ppErrorMsgs);

        private static byte[]? CompileShader(string hlsl, string entry, string target)
        {
            byte[] src = System.Text.Encoding.ASCII.GetBytes(hlsl);
            int hr = D3DCompile(src, (nuint)src.Length, "GpuShaderConverter", IntPtr.Zero, IntPtr.Zero,
                entry, target, 1u << 15, 0, out IntPtr codePtr, out IntPtr errPtr);
            if (hr < 0 || codePtr == IntPtr.Zero)
            {
                string err = errPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(errPtr) ?? "" : "";
                if (errPtr != IntPtr.Zero) Marshal.Release(errPtr);
                throw new InvalidOperationException("D3DCompile(" + entry + ") 失败 0x" + ((uint)hr).ToString("X8") + "：" + err);
            }
            try
            {
                var blob = new Vortice.Direct3D.Blob(codePtr);
                byte[] code = blob.AsBytes();
                blob.Dispose();
                return code;
            }
            finally
            {
                if (errPtr != IntPtr.Zero) Marshal.Release(errPtr);
            }
        }
    }
}
