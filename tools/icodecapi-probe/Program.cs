using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace SoulLink.ICodecApiProbe;

internal static class Program
{
    private static readonly Guid VideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MajorVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid DefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");

    public static int Main()
    {
        int exitCode = 0;
        MediaFactory.MFStartup(false);
        try
        {
            using var collection = MediaFactory.MFTEnumEx(VideoEncoder, 3, null,
                new RegisterTypeInfo { GuidMajorType = MajorVideo, GuidSubtype = H264 });
            foreach (var activate in collection)
            {
                string name = activate.GetString(FriendlyName);
                if (!name.Equals("H264 Encoder MFT", StringComparison.OrdinalIgnoreCase)) continue;
                Console.WriteLine("MFT: " + name);
                using var transform = activate.ActivateObject<IMFTransform>();
                using var api = transform.QueryInterface<CodecApi>();
                if (api == null) { Console.WriteLine("ICodecAPI: unavailable"); return 3; }
                exitCode |= Probe(api, "AVLowLatencyMode", LowLatency, CodecVariant.Bool(true), "True", "VT_BOOL");
                exitCode |= Probe(api, "AVEncMPVDefaultBPictureCount", DefaultBPictureCount, CodecVariant.UInt32(0), "0", "VT_UI4");
                if (exitCode != 0) Console.WriteLine("FAIL: at least one property did not read back as requested.");
                return exitCode;
            }
            Console.WriteLine("Microsoft software H.264 MFT not found.");
            return 2;
        }
        finally { MediaFactory.MFShutdown(); }
    }

    private static int Probe(CodecApi api, string label, Guid key, IntPtr value, string expected, string type)
    {
        try
        {
        Read(api, label, key, "before");
        int hr = api.Set(key, value);
        Console.WriteLine($"{label} SetValue({type}={expected}) HRESULT=0x{hr:X8}");
        Read(api, label, key, "after");
        using var freshApi = api.QueryInterface<CodecApi>();
            return Read(freshApi, label, key, "fresh-QI");
        }
        finally { Marshal.FreeHGlobal(value); }
    }

    private static int Read(CodecApi? api, string label, Guid key, string stage)
    {
        if (api == null || api.NativePointer == IntPtr.Zero)
        {
            Console.WriteLine($"{label} GetValue {stage}: no ICodecAPI");
            return 1;
        }
        IntPtr native = Marshal.AllocHGlobal(CodecVariant.Size);
        try
        {
            CodecVariant.Initialize(native);
            int hr = api.Get(key, native);
            if (hr < 0)
            {
                Console.WriteLine($"{label} GetValue {stage}: HRESULT=0x{hr:X8}");
                return 1;
            }
            short type = Marshal.ReadInt16(native);
            int value = Marshal.ReadInt32(native, CodecVariant.ValueOffset);
            Console.WriteLine($"{label} GetValue {stage}: HRESULT=0x{hr:X8} VT={type} value={value}");
            return type == 19 || type == 11 ? 0 : 1;
        }
        finally { Marshal.FreeHGlobal(native); }
    }
}

[Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da")]
internal sealed class CodecApi(IntPtr nativePointer) : ComObject(nativePointer)
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ValueDelegate(IntPtr self, ref Guid name, IntPtr value);

    // IUnknown occupies slots 0..2; SetValue/GetValue are slots 3 and 4.
    public int Set(Guid name, IntPtr value) => Invoke(3, name, value);
    public int Get(Guid name, IntPtr value) => Invoke(4, name, value);

    private int Invoke(int slot, Guid name, IntPtr value)
    {
        IntPtr vtbl = Marshal.ReadIntPtr(NativePointer);
        var call = Marshal.GetDelegateForFunctionPointer<ValueDelegate>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
        return call(NativePointer, ref name, value);
    }
}

internal static class CodecVariant
{
    // x64 VARIANT: vt/reserved fields = 8 bytes, scalar payload = 8 bytes.
    public static readonly int Size = IntPtr.Size + 8;
    public static readonly int ValueOffset = IntPtr.Size;

    public static void Initialize(IntPtr value) => Marshal.Copy(new byte[Size], 0, value, Size);
    public static IntPtr Bool(bool value) => Create(11, value ? -1 : 0); // VT_BOOL / VARIANT_TRUE
    public static IntPtr UInt32(uint value) => Create(19, unchecked((int)value)); // VT_UI4
    private static IntPtr Create(short type, int value)
    {
        IntPtr native = Marshal.AllocHGlobal(Size);
        Initialize(native);
        Marshal.WriteInt16(native, type);
        Marshal.WriteInt32(native, ValueOffset, value);
        return native;
    }
}
