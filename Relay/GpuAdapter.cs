using System;
using System.Collections.Generic;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SoulLink.Relay
{
    /// <summary>
    /// ★ P0-G/P0-H（2026-11-24）：GPU 适配器枚举与通用自适应路由。
    ///
    /// 用户要求产品化：不能要求所有用户关闭核显；用户可能只有核显 / AMD / Intel / NVIDIA 任意组合。
    /// 规则：
    ///   1) 枚举所有 DXGI 硬件适配器（VendorId：0x10DE=NVIDIA / 0x8086=Intel / 0x1002=AMD），
    ///      用 EnumOutputs 判断「是否有显示输出」——抓屏源必须是真有输出的那块；
    ///   2) 抓屏（DDA）优先选「有输出的 NVIDIA」（与 NVENC 同 GPU → 零拷贝可用），
    ///      否则选第一个有输出的硬件适配器（Intel/AMD/虚拟显示）；
    ///   3) NVENC 只在机器确实有 NVIDIA GPU 时尝试；Intel/AMD/核显一律走 MF（vendor 中立 MFT 枚举，
    ///      天然支持 Intel QSV / AMD AMF / NVIDIA MFT）；
    ///   4) 零拷贝只在「抓屏与编码同 GPU」时可用（物理限制，Sunshine 同构）；跨 GPU 直接 CPU 路径，
    ///      不浪费帧去试。
    /// </summary>
    internal static class GpuAdapter
    {
        public const uint NvidiaVendorId = 0x10DE;
        public const uint IntelVendorId = 0x8086;
        public const uint AmdVendorId = 0x1002;

        public sealed class AdapterInfo
        {
            public uint VendorId;
            public string Name = "";
            public bool HasOutputs;
            public bool IsSoftware;
        }

        /// <summary>拓扑缓存：进程内 GPU 拓扑基本不变，只枚举一次。</summary>
        private static List<AdapterInfo>? _cachedTopology;
        private static bool _topologyLogged;

        public static IReadOnlyList<AdapterInfo> Topology
        {
            get
            {
                if (_cachedTopology == null)
                {
                    _cachedTopology = new List<AdapterInfo>();
                    try
                    {
                        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                        for (uint i = 0; ; i++)
                        {
                            var hr = factory.EnumAdapters1(i, out var adapter);
                            if (hr.Failure || adapter == null) break;
                            using (adapter)
                            {
                                var desc = adapter.Description1;
                                _cachedTopology.Add(new AdapterInfo
                                {
                                    VendorId = desc.VendorId,
                                    Name = desc.Description.Trim(),
                                    HasOutputs = AdapterHasOutputs(adapter),
                                    IsSoftware = desc.Flags.HasFlag(AdapterFlags.Software)
                                });
                            }
                        }
                    }
                    catch { }
                }
                return _cachedTopology;
            }
        }

        /// <summary>机器上是否存在 NVIDIA GPU（无论是否有输出）。</summary>
        public static bool HasNvidiaAdapter()
        {
            foreach (var a in Topology) if (a.VendorId == NvidiaVendorId && !a.IsSoftware) return true;
            return false;
        }

        /// <summary>返回 NVIDIA 硬件适配器（新引用由调用方持有）；没有则 null。</summary>
        public static IDXGIAdapter1? PickNvidiaAdapter()
        {
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint i = 0; ; i++)
                {
                    var hr = factory.EnumAdapters1(i, out var adapter);
                    if (hr.Failure || adapter == null) break;
                    var desc = adapter.Description1;
                    if (desc.VendorId == NvidiaVendorId && !desc.Flags.HasFlag(AdapterFlags.Software)) return adapter;
                    adapter.Dispose();
                }
            }
            catch { }
            return null;
        }

        /// <summary>★ P0-H 可观测性：打印一次拓扑（用户排障全靠这行）。</summary>
        public static void LogTopologyOnce()
        {
            if (_topologyLogged) return;
            _topologyLogged = true;
            var parts = new List<string>();
            int idx = 0;
            foreach (var a in Topology)
                parts.Add("adapter" + idx++ + "=" + VendorName(a.VendorId) + "(" + VendorHex(a.VendorId) + ")" + (a.HasOutputs ? " 有输出" : " 无输出") + (a.IsSoftware ? "[软件]" : ""));
            RelayAgent.LogQuiet("[gpu] 枚举到 " + Topology.Count + " 块适配器: " + string.Join(", ", parts));
        }

        public static string VendorHex(uint vendorId) => "0x" + vendorId.ToString("X4");

        public static string VendorName(uint vendorId) => vendorId switch
        {
            NvidiaVendorId => "NVIDIA",
            IntelVendorId => "Intel",
            AmdVendorId => "AMD",
            _ => "Other"
        };

        /// <summary>
        /// 抓屏候选适配器（按优先级排序，返回新引用由调用方持有）：
        /// 1) 有输出的 NVIDIA（与 NVENC 同 GPU → 零拷贝可用）；2) 其余有输出的硬件适配器（枚举序）；
        /// 3) 无输出的 NVIDIA（最后兜底，DuplicateOutput 大概率失败，但保留机会）。
        /// </summary>
        public static List<IDXGIAdapter1> PickCaptureCandidates()
        {
            var result = new List<IDXGIAdapter1>();
            try
            {
                using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                for (uint i = 0; ; i++)
                {
                    var hr = factory.EnumAdapters1(i, out var adapter);
                    if (hr.Failure || adapter == null) break;
                    var desc = adapter.Description1;
                    bool isSoftware = desc.Flags.HasFlag(AdapterFlags.Software);
                    bool hasOutputs = AdapterHasOutputs(adapter);
                    if (!isSoftware && hasOutputs && desc.VendorId == NvidiaVendorId)
                        result.Insert(0, adapter);            // NVIDIA 有输出 → 最优先
                    else if (!isSoftware && hasOutputs)
                        result.Add(adapter);                  // Intel/AMD 等有输出 → 次之
                    else
                        adapter.Dispose();                    // 软件/无输出适配器不作为抓屏候选
                }
            }
            catch { }
            return result;
        }

        private static bool AdapterHasOutputs(IDXGIAdapter1 adapter)
        {
            try
            {
                return adapter.EnumOutputs(0, out IDXGIOutput output).Success && output != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// 在指定适配器上创建 D3D11 硬件设备（adapter=null 即默认适配器，旧行为）。
        /// pAdapter 非空时 DriverType 必须为 Unknown。
        /// </summary>
        public static bool TryCreateDevice(IDXGIAdapter1? adapter, out ID3D11Device? device, out ID3D11DeviceContext? context, out string error)
        {
            device = null; context = null; error = "";
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            Result hr = adapter == null
                ? Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context)
                : Vortice.Direct3D11.D3D11.D3D11CreateDevice(adapter.NativePointer, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels, out device, out _, out context);
            if (!hr.Success || device == null)
            {
                error = "D3D11 硬件设备失败 0x" + hr.Code.ToString("X8");
                return false;
            }
            return true;
        }
    }
}
