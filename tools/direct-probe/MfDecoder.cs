// ============================================================================
// MfDecoder —— MF H.264 解码器：Annex-B AU 进，NV12 出。
// 枚举 MFT_CATEGORY_VIDEO_DECODER（硬解优先，MF 自选——与浏览器
// prefer-hardware 语义同构）；尺寸从输出类型协商读回（不手写 SPS 解析）。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DirectProbe;

sealed class MfDecoder : IDisposable
{
    static readonly Guid MFT_CATEGORY_VIDEO_DECODER = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");   // Video decoders（注册表 mfapi.h 实证）
    static readonly Guid MajorVideo = new("73646976-0000-0010-8000-00AA00389B71");
    static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    static readonly Guid NV12 = new("3231564e-0000-0010-8000-00AA00389B71");

    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D60);

    IMFTransform _dec;
    bool _configured, _disposed;
    int _width, _height, _stride;
    long _nextSampleTime;
    int _dbg, _dbg2;
    readonly List<IMFSample> _inputSamples = new();

    public sealed record OutFrame(byte[] Data, int Width, int Height, int Stride, long DecodedAt);

    static IMFTransform? FindDecoderMft()
    {
        // flags：SYNCMFT(1)+ASYNCMFT(2)。枚举结果按注册顺序（不保证 H264 优先），
        // 所以逐个「激活 + 试探 SetInputType(H264)」，成功者即目标解码器。
        // 无 filter 枚举（硬解 MFT 注册常缺 input subtype，靠 SetInputType 试探筛选）
        // 1|2|4 = SYNCMFT|ASYNCMFT|HARDWARE（硬解 MFT 必须 HARDWARE 标志才枚举得到）
        using var collection = MediaFactory.MFTEnumEx(MFT_CATEGORY_VIDEO_DECODER, 7, null, null);
        foreach (var activate in collection)
        {
            using (activate)
            {
                string name = "?";
                try { name = activate.GetString(new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3")); } catch { }
                bool isAsync = false;
                try { isAsync = activate.GetUInt32(TransformAttributeKeys.TransformAsync) != 0; } catch { }
                Console.WriteLine("[decoder] 候选 " + name + " async=" + isAsync);
                _ = name;
                if (name.IndexOf("H264", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    var tf = activate.ActivateObject<IMFTransform>();
                    if (isAsync) tf.Attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, true);
                    Console.WriteLine("[decoder] 使用 MFT: " + name + " (async=" + isAsync + ")");
                    return tf;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[decoder]   跳过 " + name + ": " + ex.Message);
                }
            }
        }
        return null;
    }

    public MfDecoder()
    {
        _dec = FindDecoderMft();
        if (_dec == null) throw new InvalidOperationException("无 H.264 解码 MFT（枚举 0 个，详见 --list-mft 诊断输出）");
        Console.WriteLine("[decoder] MFT 已激活");
    }

    public IEnumerable<OutFrame> Decode(byte[] au, int len)
    {
        if (!_configured) Configure();
        // 先排空上一帧可能残留的输出（防 NOTACCEPTING：解码器输出未取走时不收新输入）
        while (true)
        {
            long t1;
            OutFrame? stale;
            int hrStale = DrainOne(out stale, out t1);
            if (stale != null) yield return stale;
            if (hrStale == MF_E_TRANSFORM_NEED_MORE_INPUT || hrStale < 0) break;
        }
        var sample = CreateSample(au, len);
        try { _dec.ProcessInput(0, sample, 0); }
        catch (Exception ex)
        {
            if (_dbg++ < 5) Console.WriteLine("[decoder] ProcessInput 失败(len=" + len + "): " + ex.Message);
        }
        finally
        {
            // ⭐ 不能立刻 Dispose：同步 MFT 也可能在 ProcessInput 返回后仍异步访问 sample 缓冲
            // （微软 H264 解码器实测：立即释放会导致 NEED_MORE_INPUT 死循环 + NOTACCEPTING）。
            // 挂到延迟释放队列，保留最近 5 个输入 sample。
            _inputSamples.Add(sample);
            while (_inputSamples.Count > 5) { try { _inputSamples[0].Dispose(); } catch { } _inputSamples.RemoveAt(0); }
        }
        while (true)
        {
            long t1;
            OutFrame? frame;
            int hr = DrainOne(out frame, out t1);
            if (frame != null) yield return frame;
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                ConfigureOutputTypeOnly();
                continue;
            }
            if (frame == null && hr < 0) break;
        }
        // ⭐ 修解码不吐帧：同步 MFT 输出可能在「两次 ProcessOutput 之间」才就绪——强制再拉一轮
        for (int retry = 0; retry < 2; retry++)
        {
            long t1;
            OutFrame? frame;
            int hr = DrainOne(out frame, out t1);
            if (frame != null) yield return frame;
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) break;
        }
    }

    void ConfigureOutputTypeOnly()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _dec.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            if (t == null) break;
            using (t)
            {
                Guid sub = Guid.Empty;
                try { sub = t.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { }
                if (sub == NV12)
                {
                    _dec.SetOutputType(0, t, 0);
                    ReadDimensions();
                    Console.WriteLine("[decoder] 流变更后重设 NV12 " + _width + "x" + _height);
                    break;
                }
            }
        }
    }

    void Configure()
    {
        // ★ 关键修复尝试：先枚举输出类型（解码器可能要「先输出后输入」的协商顺序）
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _dec.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            if (t == null) break;
            using (t)
            {
                Guid sub = Guid.Empty;
                try { sub = t.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { }
                if (sub == NV12) { _dec.SetOutputType(0, t, 0); break; }
            }
        }
        using var inType = MediaFactory.MFCreateMediaType();
        inType.Set(MediaTypeAttributeKeys.MajorType, MajorVideo);
        inType.Set(MediaTypeAttributeKeys.Subtype, H264);
        // 输入类型带完整属性（部分 MFT 拒绝裸类型/等待更多信息）
        if (_width > 0 && _height > 0)
        {
            inType.Set(MediaTypeAttributeKeys.FrameSize, ((long)(uint)_width << 32) | (uint)_height);
            inType.Set(MediaTypeAttributeKeys.FrameRate, ((long)30 << 32) | 1);
        }
        _dec.SetInputType(0, inType, 0);

        // 输出类型：枚举可用类型找 NV12
        for (int i = 0; ; i++)
        {
            IMFMediaType t;
            try { t = _dec.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            if (t == null) break;
            using (t)
            {
                Guid sub = Guid.Empty;
                try { sub = t.GetGUID(MediaTypeAttributeKeys.Subtype); } catch { }
                if (sub == new Guid("3231564e-0000-0010-8000-00AA00389B71"))
                {
                    _dec.SetOutputType(0, t, 0);
                    break;
                }
            }
        }
        ReadDimensions();
        _dec.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _dec.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _configured = true;
        Console.WriteLine("[decoder] 已协商 NV12 " + _width + "x" + _height);
    }

    void ReadDimensions()
    {
        using var cur = _dec.GetOutputCurrentType(0);
        try
        {
            long fs = (long)cur.GetUInt64(MediaTypeAttributeKeys.FrameSize);
            _width = (int)(fs >> 32); _height = (int)(fs & 0xffffffff);
        }
        catch { }
        // DefaultStride 属性走 PROPVARIANT 太底层；stride 交给 DrainOne 按输出长度反推
        if (_stride == 0) _stride = _width;
    }

    IMFSample CreateSample(byte[] au, int len)
    {
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(len);
        buffer.Lock(out IntPtr pData, out _, out _);
        try { Marshal.Copy(au, 0, pData, len); }
        finally { buffer.Unlock(); }
        buffer.CurrentLength = len;
        sample.AddBuffer(buffer);
        sample.SampleTime = _nextSampleTime;
        sample.SampleDuration = 333333;   // 30fps 帧时长（μs）
        _nextSampleTime += 333333;
        return sample;
    }

    int DrainOne(out OutFrame? frame, out long t1)
    {
        frame = null; t1 = 0;
        // 每次调用新建输出 sample（本解码循环单帧可能多次 ProcessOutput，复用会破坏 MFT 内部引用 → 崩溃）
        int size = Math.Max(1 << 22, _width * _height * 2);
        var sample = MediaFactory.MFCreateSample();
        var buffer = MediaFactory.MFCreateMemoryBuffer(size);
        sample.AddBuffer(buffer);
        var outBuf = new OutputDataBuffer { StreamID = 0, Sample = sample };
        try
        {
            var status = new ProcessOutputStatus();
            Result r = _dec.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuf, out status);
            t1 = Stopwatch.GetTimestamp();
            int hr = r.Code;
            IMFSample? got = outBuf.Sample;
            outBuf.Sample = null;
            IMFSample? real = got ?? sample;
            if (!r.Success || real == null)
            {
                if (!ReferenceEquals(real, sample)) real?.Dispose();
                return hr == 0 ? MF_E_TRANSFORM_NEED_MORE_INPUT : hr;
            }
            try
            {
                var bufOut = real.GetBufferByIndex(0);
                if (bufOut == null) return hr;
                bufOut.Lock(out IntPtr p, out _, out _);
                try
                {
                    int len = bufOut.CurrentLength;
                    int stride = _height > 0 ? (len * 2) / (_height * 3) : _width;
                    if (stride < _width) stride = _width;
                    _stride = stride;
                    var data = new byte[len];
                    Marshal.Copy(p, data, 0, len);
                    frame = new OutFrame(data, _width, _height, stride, t1);
                    return hr;
                }
                finally { bufOut.Unlock(); }
            }
            finally
            {
                real.RemoveAllBuffers();
                if (!ReferenceEquals(real, sample)) real.Dispose();
            }
        }
        finally { outBuf.Sample = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _dec.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch { }
        try { _dec.Dispose(); } catch { }
    }
}
