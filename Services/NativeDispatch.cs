using System;
using System.Runtime.InteropServices;

namespace SoulLink.Services
{
    /// <summary>
    /// 原生 IDispatch 桥。
    ///
    /// 背景：MSRDP 的 ClearTextPassword 属于 IMsTscNonScriptable，
    /// 这个接口**不在主 IDispatch 的分发表里**（GetIDsOfNames 返回 0x80020006），
    /// 而 .NET Core 又不支持 [ComImport] 的 InterfaceIsIDispatch 映射（必抛 NotImplementedException）。
    ///
    /// ★ 教训（实测）：直接对控件主 IDispatch 猜 DISPID 是错的——
    ///   DISPID=1 落到的是 **Server**，不是 ClearTextPassword。
    ///   必须 QI 到 IMsTscNonScriptable 的 IID 上，再用**那个接口自己的** IDispatch 分发表。
    /// </summary>
    public static class NativeDispatch
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DISPPARAMS
        {
            public IntPtr rgvarg;
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct VariantX64
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr p;
        }

        private const ushort VT_BSTR = 8;
        private const int DISPATCH_PROPERTYPUT = 4;
        private const int DISPATCH_METHOD = 1;
        private const int DISPID_PROPERTYPUT = -3;
        private const uint LOCALE_USER_DEFAULT = 0x400;

        /// <summary>IMsTscNonScriptable 的 IID</summary>
        public static readonly Guid IID_IMsTscNonScriptable = new Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E");

        /// <summary>IMsTscSecuredSettings 的 IID —— 密码实际挂在这上面</summary>
        public static readonly Guid IID_IMsTscSecuredSettings = new Guid("C9D65441-2267-4F7C-8B72-7E1E6E5F2A68");
        /// <summary>IMsTscSecuredSettings2 的 IID</summary>
        public static readonly Guid IID_IMsTscSecuredSettings2 = new Guid("4BDB4FDE-4A5E-4B5B-9C4B-8E4E2D0F5C21");

        /// <summary>主 IDispatch 的 IID</summary>
        private static readonly Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelQueryInterface(IntPtr p, ref Guid riid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelGetIDsOfNames(IntPtr p, ref Guid riid, IntPtr[] rgszNames, uint cNames, uint lcid, int[] rgDispId);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelInvoke(IntPtr p, int dispId, ref Guid riid, uint lcid, ushort wFlags,
            ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

        private static IntPtr VtblFn(IntPtr pUnk, int slot)
            => Marshal.ReadIntPtr(Marshal.ReadIntPtr(pUnk), slot * IntPtr.Size);

        /// <summary>在指定接口上按名字查 DISPID。返回 HRESULT。</summary>
        public static int TryGetDispId(IntPtr iface, string name, out int dispId)
        {
            dispId = -1;
            if (iface == IntPtr.Zero) return unchecked((int)0x80004003);
            IntPtr[] names = { Marshal.StringToBSTR(name) };
            int[] ids = { -1 };
            Guid iidNull = Guid.Empty;
            try
            {
                var fn = (DelGetIDsOfNames)Marshal.GetDelegateForFunctionPointer(VtblFn(iface, 5), typeof(DelGetIDsOfNames));
                int hr = fn(iface, ref iidNull, names, 1, LOCALE_USER_DEFAULT, ids);
                if (hr == 0) { dispId = ids[0]; return 0; }
                return hr;
            }
            catch { return unchecked((int)0x80004005); }
            finally { Marshal.FreeBSTR(names[0]); }
        }

        /// <summary>按 DISPID 给一个 BSTR 属性赋值。返回 HRESULT，0 = 成功。</summary>
        public static int SetPropertyBstr(IntPtr iface, int dispId, string value)
        {
            if (iface == IntPtr.Zero) return unchecked((int)0x80004003);

            var v = new VariantX64 { vt = VT_BSTR, p = Marshal.StringToBSTR(value ?? string.Empty) };
            IntPtr pArg = Marshal.AllocHGlobal(Marshal.SizeOf<VariantX64>());
            IntPtr pNamed = Marshal.AllocHGlobal(4);
            IntPtr pExcep = Marshal.AllocHGlobal(128);
            IntPtr pArgErr = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.StructureToPtr(v, pArg, false);
                Marshal.WriteInt32(pNamed, DISPID_PROPERTYPUT);

                var dp = new DISPPARAMS { rgvarg = pArg, rgdispidNamedArgs = pNamed, cArgs = 1, cNamedArgs = 1 };
                Guid iidNull = Guid.Empty;
                var fn = (DelInvoke)Marshal.GetDelegateForFunctionPointer(VtblFn(iface, 6), typeof(DelInvoke));
                return fn(iface, dispId, ref iidNull, LOCALE_USER_DEFAULT,
                          DISPATCH_PROPERTYPUT, ref dp, IntPtr.Zero, pExcep, pArgErr);
            }
            catch { return unchecked((int)0x80004005); }
            finally
            {
                if (v.p != IntPtr.Zero) Marshal.FreeBSTR(v.p);
                Marshal.FreeHGlobal(pArg);
                Marshal.FreeHGlobal(pNamed);
                Marshal.FreeHGlobal(pExcep);
                Marshal.FreeHGlobal(pArgErr);
            }
        }

        /// <summary>
        /// 设置 RDP 扩展属性（DesktopScaleFactor / DeviceScaleFactor 等）。
        ///
        /// 为什么不用 [ComImport] IMsRdpExtendedSettings：.NET Core 下手写接口布局对不上，
        /// 一调就抛 0x8000FFFF E_UNEXPECTED（实测）。改成纯 IDispatch late-bound 调用：
        /// 先 GetIDsOfNames 拿 DISPID，再带一个 VARIANT 参数 Invoke。
        /// </summary>
        /// <summary>最近一次扩展属性设置失败的原因（排查用）。</summary>
        public static string LastError { get; private set; } = "";

        public static bool SetExtendedProperty(object ocx, string propertyName, object value)
        {
            if (ocx == null) return false;
            IntPtr disp = IntPtr.Zero;
            try
            {
                disp = Marshal.GetIDispatchForObject(ocx);
                if (disp == IntPtr.Zero) return false;

                int hrId = TryGetDispId(disp, propertyName, out int dispid);
                if (hrId != 0 || dispid < 0)
                {
                    // 扩展属性走的是 set_Property 方法
                    hrId = TryGetDispId(disp, "set_Property", out dispid);
                    if (hrId != 0 || dispid < 0)
                    {
                        System.Diagnostics.Debug.WriteLine("[NativeDispatch] 找不到 " + propertyName
                            + " / set_Property，hr=0x" + hrId.ToString("X8"));
                        LastError = "分发表里没有 " + propertyName + " / set_Property (0x" + hrId.ToString("X8") + ")";
                        return false;
                    }
                }

                // 参数：属性名 + 值
                int hr = InvokeWithNameAndValue(disp, dispid, propertyName, value);
                if (hr != 0) LastError = "Invoke 返回 0x" + hr.ToString("X8") + " (dispid=" + dispid + ")";
                return hr == 0;
            }
            catch { return false; }
            finally { if (disp != IntPtr.Zero) { try { Marshal.Release(disp); } catch { } } }
        }

        /// <summary>带 (BSTR 属性名, VARIANT 值) 两个参数的方法调用。</summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelQi(IntPtr p, ref Guid riid, out IntPtr ppv);

        /// <summary>IMsRdpExtendedSettings 的 IID</summary>
        public static readonly Guid IID_IMsRdpExtendedSettings = new Guid("302D8188-0052-4807-806A-362B628F9AC5");

        /// <summary>
        /// IMsRdpExtendedSettings::set_Property 的 vtable 调用。
        /// 接口布局（IDispatch 派生）：
        ///   0-2 IUnknown, 3-6 IDispatch, 7 get_Property, 8 set_Property
        /// </summary>
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelSetProperty(IntPtr p, IntPtr bstrName, IntPtr pVarValue);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DelInvoke2(IntPtr p, int id, ref Guid riid, uint lcid, ushort flags,
            IntPtr pParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

        /// <summary>
        /// 通过 IMsRdpExtendedSettings 设置扩展属性（vtable 直调）。
        /// 1Remote 是直接强转接口，这里手写等价实现。
        /// </summary>
        public static bool SetExtendedPropertyVtable(object ocx, string propertyName, object value)
        {
            if (ocx == null) return false;
            IntPtr unk = IntPtr.Zero, ext = IntPtr.Zero, bstr = IntPtr.Zero, pVar = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(ocx);
                Guid iid = IID_IMsRdpExtendedSettings;
                var qi = (DelQi)Marshal.GetDelegateForFunctionPointer(VtblFn(unk, 0), typeof(DelQi));
                int hr = qi(unk, ref iid, out ext);
                if (hr != 0 || ext == IntPtr.Zero)
                {
                    LastError = "QI(IMsRdpExtendedSettings) 失败 0x" + hr.ToString("X8");
                    return false;
                }

                pVar = Marshal.AllocHGlobal(24);
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pVar, 0);
                if (value is uint u) { Marshal.WriteInt16(pVar, 0, 19); Marshal.WriteInt32(pVar, 8, (int)u); }
                else if (value is int i2) { Marshal.WriteInt16(pVar, 0, 3); Marshal.WriteInt32(pVar, 8, i2); }
                else { Marshal.WriteInt16(pVar, 0, 8); Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value?.ToString() ?? "")); }

                bstr = Marshal.StringToBSTR(propertyName ?? "");

                // slot 8 = set_Property(BSTR, VARIANT*)
                var setProp = (DelSetProperty)Marshal.GetDelegateForFunctionPointer(VtblFn(ext, 8), typeof(DelSetProperty));
                hr = setProp(ext, bstr, pVar);
                if (hr != 0) LastError = "set_Property(" + propertyName + ") 返回 0x" + hr.ToString("X8");
                return hr == 0;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
            finally
            {
                if (bstr != IntPtr.Zero) { try { Marshal.FreeBSTR(bstr); } catch { } }
                if (pVar != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr s = Marshal.ReadIntPtr(pVar, 8);
                        if (Marshal.ReadInt16(pVar, 0) == 8 && s != IntPtr.Zero) Marshal.FreeBSTR(s);
                    }
                    catch { }
                    Marshal.FreeHGlobal(pVar);
                }
                if (ext != IntPtr.Zero) { try { Marshal.Release(ext); } catch { } }
                if (unk != IntPtr.Zero) { try { Marshal.Release(unk); } catch { } }
            }
        }

        private static int InvokeWithNameAndValue(IntPtr iface, int dispId, string name, object value)
        {
            IntPtr args = IntPtr.Zero, pName = IntPtr.Zero, pVal = IntPtr.Zero, pDp = IntPtr.Zero;
            IntPtr pExcep = IntPtr.Zero, pArgErr = IntPtr.Zero;
            try
            {
                // 两个 VARIANT（顺序：值在前，名字在后——DISPPARAMS 是倒序）
                args = Marshal.AllocHGlobal(24 * 2);
                for (int i = 0; i < 48; i++) Marshal.WriteByte(args, 0);

                // VARIANT[0] = 值
                WriteVariant(args, value);
                // VARIANT[1] = 属性名（BSTR）
                IntPtr vb = Marshal.StringToBSTR(name);
                Marshal.WriteInt16(args, 24, 8);                        // vt = VT_BSTR
                Marshal.WriteIntPtr(args, 24 + 8, vb);
                pName = vb;

                // DISPPARAMS
                pDp = Marshal.AllocHGlobal(Marshal.SizeOf<DISPPARAMS>());
                var dp = new DISPPARAMS
                {
                    rgvarg = args,
                    rgdispidNamedArgs = IntPtr.Zero,
                    cArgs = 2,
                    cNamedArgs = 0
                };
                Marshal.StructureToPtr(dp, pDp, false);

                pExcep = Marshal.AllocHGlobal(128);
                pArgErr = Marshal.AllocHGlobal(4);
                Guid iidNull = Guid.Empty;
                var fn = (DelInvoke2)Marshal.GetDelegateForFunctionPointer(VtblFn(iface, 6), typeof(DelInvoke2));
                return fn(iface, dispId, ref iidNull, LOCALE_USER_DEFAULT, DISPATCH_METHOD,
                          pDp, IntPtr.Zero, pExcep, pArgErr);
            }
            catch { return unchecked((int)0x80004005); }
            finally
            {
                if (pName != IntPtr.Zero) { try { Marshal.FreeBSTR(pName); } catch { } }
                if (args != IntPtr.Zero) Marshal.FreeHGlobal(args);
                if (pDp != IntPtr.Zero) Marshal.FreeHGlobal(pDp);
                if (pExcep != IntPtr.Zero) Marshal.FreeHGlobal(pExcep);
                if (pArgErr != IntPtr.Zero) Marshal.FreeHGlobal(pArgErr);
            }
        }

        private static void WriteVariant(IntPtr at, object value)
        {
            // at 指向一个 24 字节的 VARIANT
            if (value is uint u)
            {
                Marshal.WriteInt16(at, 0, 19);                       // vt = VT_UI4
                Marshal.WriteInt32(at, 8, (int)u);
            }
            else if (value is int i)
            {
                Marshal.WriteInt16(at, 0, 3);                        // vt = VT_I4
                Marshal.WriteInt32(at, 8, i);
            }
            else
            {
                Marshal.WriteInt16(at, 0, 8);                        // vt = VT_BSTR
                Marshal.WriteIntPtr(at, 8, Marshal.StringToBSTR(value?.ToString() ?? ""));
            }
        }

        /// <summary>
        /// 给 RDP 控件设置明文密码。
        /// 做法：QI 到 IMsTscNonScriptable，在那个接口上查 ClearTextPassword 的 DISPID，再 Invoke。
        /// </summary>
        public static bool SetRdpPassword(object ocx, string password, out string detail)
        {
            detail = "";
            if (ocx == null) { detail = "控件为空"; return false; }

            IntPtr unk = IntPtr.Zero;
            IntPtr secured = IntPtr.Zero;
            try
            {
                unk = Marshal.GetIUnknownForObject(ocx);
                if (unk == IntPtr.Zero) { detail = "拿不到 IUnknown"; return false; }

                var qi = (DelQueryInterface)Marshal.GetDelegateForFunctionPointer(VtblFn(unk, 0), typeof(DelQueryInterface));

                // 依次尝试：IMsTscNonScriptable → IMsTscSecuredSettings，各自查自己的分发表
                var probes = new (string name, Guid iid)[]
                {
                    ("IMsTscNonScriptable", IID_IMsTscNonScriptable),
                    ("IMsTscSecuredSettings", IID_IMsTscSecuredSettings),
                    ("IMsTscSettings", new Guid("F9BD49A3-08A2-4A62-9AD6-9C5E0C5E2C5A")),
                    ("IMsRdpClientNonScriptable", new Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")),
                    ("IMsRdpClientNonScriptable2", new Guid("17A5E535-4072-4E60-AF3A-3A9A0F2B4A3F")),
                };

                var notes = new System.Collections.Generic.List<string>();

                foreach (var p in probes)
                {
                    Guid iid = p.iid;
                    IntPtr itf = IntPtr.Zero;
                    int hrQi = qi(unk, ref iid, out itf);
                    if (hrQi != 0 || itf == IntPtr.Zero)
                    {
                        notes.Add(p.name + ":QI=0x" + hrQi.ToString("X8"));
                        continue;
                    }

                    try
                    {
                        int hrId = TryGetDispId(itf, "ClearTextPassword", out int dispId);
                        if (hrId == 0 && dispId >= 0)
                        {
                            int hr = SetPropertyBstr(itf, dispId, password ?? "");
                            detail = p.name + " dispid=" + dispId + " hr=0x" + hr.ToString("X8");
                            if (hr == 0) return true;
                            notes.Add(detail);
                        }
                        else
                        {
                            notes.Add(p.name + ":无该名字(0x" + hrId.ToString("X8") + ")");
                        }
                    }
                    finally { Marshal.Release(itf); }
                }

                // 退路：主 IDispatch 上按名字查（某些版本会暴露）
                detail = string.Join("｜", notes);
                Guid iidDisp = IID_IDispatch;
                if (qi(unk, ref iidDisp, out IntPtr disp) == 0 && disp != IntPtr.Zero)
                {
                    try
                    {
                        if (TryGetDispId(disp, "ClearTextPassword", out int id2) == 0 && id2 >= 0)
                        {
                            int hr = SetPropertyBstr(disp, id2, password ?? "");
                            detail += "｜主分发表 dispid=" + id2 + " hr=0x" + hr.ToString("X8");
                            return hr == 0;
                        }
                        detail += "｜主分发表也没有该属性";
                    }
                    finally { Marshal.Release(disp); }
                }
                return false;
            }
            catch (Exception ex)
            {
                detail = "异常: " + ex.Message;
                return false;
            }
            finally
            {
                if (secured != IntPtr.Zero) { try { Marshal.Release(secured); } catch { } }
                if (unk != IntPtr.Zero) { try { Marshal.Release(unk); } catch { } }
            }
        }
    }


    /// <summary>手写 AxHost：宿主 MSRDP ActiveX 控件（本机没有 aximp，无法生成包装类）。</summary>
    public sealed class RdpAxHost : System.Windows.Forms.AxHost
    {
        private const int WM_MOUSEACTIVATE = 0x0021;
        private const int WM_GETOBJECT = 0x003D;

        public object Ocx { get; private set; }

        /// <summary>控件创建完成（句柄就绪）</summary>
        public event Action<RdpAxHost>? Ready;

        public RdpAxHost(string clsid) : base(clsid) { }

        protected override void AttachInterfaces()
        {
            try { Ocx = GetOcx(); } catch { Ocx = null; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                var ocx = Ocx;
                Ocx = null!;
                if (ocx != null && System.Runtime.InteropServices.Marshal.IsComObject(ocx))
                {
                    try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(ocx); } catch { }
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>必须显式创建过一次句柄，GetOcx 才有值。</summary>
        public void EnsureCreated()
        {
            CreateControl();
            Ready?.Invoke(this);
        }

        /// <summary>
        /// 消息处理。两处都是照 1Remote 的做法（见 HANDOFF 12.1）：
        ///
        /// ① WM_MOUSEACTIVATE —— **修「点了控件但键盘没反应」**
        ///    RDP 控件被点击后不主动抢焦点，键盘事件就进不去目标会话。
        ///    这里检测到 mx 激活但没焦点时主动 Focus()。
        ///
        /// ② WM_GETOBJECT —— 防崩溃
        ///    有 UIA / 读屏软件探测这个控件时会崩，返回 -1 是 1Remote 实测有效的做法。
        /// </summary>
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_GETOBJECT)
            {
                m.Result = (IntPtr)(-1);
                return;
            }

            if (m.Msg == WM_MOUSEACTIVATE && !ContainsFocus)
            {
                try { Focus(); } catch { }
            }

            base.WndProc(ref m);
        }

        /// <summary>让控件主动拿到键盘焦点。</summary>
        public void GrabFocus()
        {
            try
            {
                if (ContainsFocus) return;
                if (!IsHandleCreated) CreateControl();
                Focus();
            }
            catch { }
        }
    }
}
