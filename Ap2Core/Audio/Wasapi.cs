using System.Runtime.InteropServices;

namespace Ap2Core.Audio;

// Core Audio (WASAPI) COM 声明：只含回环抓取所需的接口，vtable 顺序必须与系统头文件一致
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumeratorCom { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint devices);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint props);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
    [PreserveSig] int SetValue(ref PropertyKey key, IntPtr value);
    [PreserveSig] int Commit();
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, ref Guid audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint numFramesRead);
    [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
}

/// <summary>设备混音格式（从 WAVEFORMATEX / WAVEFORMATEXTENSIBLE 解析）</summary>
public sealed record MixFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat, int ChannelMask)
{
    public override string ToString() => $"{SampleRate}Hz {Channels}声道 {BitsPerSample}位 {(IsFloat ? "浮点" : "整数")} 声道掩码=0x{ChannelMask:X}";
}

/// <summary>Windows 播放端点。ID 可稳定保存，名称只用于界面显示。</summary>
public sealed record AudioEndpoint(string Id, string Name, bool IsDefault);

/// <summary>
/// WASAPI 回环抓取：把默认播放端点（或指定端点）正在播放的混音抓下来，转成立体声浮点（设备原采样率）回调出去。
/// 回环流在系统静音/无声时不会产生数据包，调用方需要自行补静音。
/// </summary>
public sealed class LoopbackCapture : IDisposable
{
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    readonly string? _deviceId;
    readonly Action<string> _log;
    Thread? _thread;
    volatile bool _stop;
    readonly ManualResetEventSlim _ready = new(false);
    Exception? _initError;

    public MixFormat? Format { get; private set; }
    public string DeviceId { get; private set; } = "";
    /// <summary>每批数据：交错的立体声浮点 [-1,1]，设备原采样率</summary>
    public event Action<float[], int>? DataAvailable;
    /// <summary>抓取意外结束（例如设备被拔出/切换）</summary>
    public event Action<string>? Stopped;
    public long FramesCaptured;

    public LoopbackCapture(Action<string> log, string? deviceId = null)
    {
        _log = log;
        _deviceId = deviceId;
    }

    /// <summary>当前默认播放设备的 ID（无设备时为 null）；可在任意线程调用</summary>
    public static string? GetDefaultRenderId()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(0, 0, out dev) != 0 || dev == null) return null;
            dev.GetId(out var id);
            return id;
        }
        catch { return null; }
        finally
        {
            try { if (dev != null) Marshal.ReleaseComObject(dev); } catch { }
            try { if (en != null) Marshal.ReleaseComObject(en); } catch { }
        }
    }

    /// <summary>列出当前启用的 Windows 播放端点，默认设备排在最前。</summary>
    public static IReadOnlyList<AudioEndpoint> GetRenderEndpoints()
    {
        IMMDeviceEnumerator? en = null;
        IMMDeviceCollection? collection = null;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(0, 1, out collection)); // eRender, DEVICE_STATE_ACTIVE
            string? defaultId = GetDefaultRenderId();
            Marshal.ThrowExceptionForHR(collection.GetCount(out uint count));
            var result = new List<AudioEndpoint>((int)count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? dev = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(i, out dev));
                    Marshal.ThrowExceptionForHR(dev.GetId(out string id));
                    string name = ReadFriendlyName(dev);
                    if (string.IsNullOrWhiteSpace(name)) name = id;
                    result.Add(new AudioEndpoint(id, name.Trim(), id == defaultId));
                }
                catch
                {
                    // 单个端点刚好被拔出时不影响其余端点。
                }
                finally
                {
                    if (dev != null) Marshal.ReleaseComObject(dev);
                }
            }
            return result
                .OrderByDescending(x => x.IsDefault)
                .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException("无法列举 Windows 播放设备: " + e.Message, e);
        }
        finally
        {
            if (collection != null) Marshal.ReleaseComObject(collection);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    static string ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        IntPtr value = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out store)); // STGM_READ
            var key = new PropertyKey
            {
                FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                PropertyId = 14, // PKEY_Device_FriendlyName
            };
            value = Marshal.AllocHGlobal(24);
            ZeroMemory(value, 24);
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, value));
            ushort vt = (ushort)Marshal.ReadInt16(value, 0);
            if (vt != 31) return ""; // VT_LPWSTR
            IntPtr text = Marshal.ReadIntPtr(value, 8);
            return text == IntPtr.Zero ? "" : Marshal.PtrToStringUni(text) ?? "";
        }
        finally
        {
            if (value != IntPtr.Zero)
            {
                try { PropVariantClear(value); } catch { }
                Marshal.FreeHGlobal(value);
            }
            if (store != null) Marshal.ReleaseComObject(store);
        }
    }

    static void ZeroMemory(IntPtr destination, int length)
    {
        for (int i = 0; i < length; i++) Marshal.WriteByte(destination, i, 0);
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(IntPtr value);

    /// <summary>启动抓取线程并等待初始化完成；失败抛异常</summary>
    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WasapiLoopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.Wait(5000)) throw new TimeoutException("WASAPI 回环初始化超时");
        if (_initError != null) throw new InvalidOperationException("WASAPI 回环初始化失败: " + _initError.Message, _initError);
    }

    static unsafe MixFormat ParseFormat(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int ch = Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int bits = Marshal.ReadInt16(p, 14);
        int cb = Marshal.ReadInt16(p, 16);
        bool isFloat = tag == 3;
        int mask = 0;
        if (tag == 0xFFFE && cb >= 22)
        {
            mask = Marshal.ReadInt32(p, 20);
            // SubFormat 的前 4 字节：1=PCM, 3=IEEE_FLOAT
            int sub = Marshal.ReadInt32(p, 24);
            isFloat = sub == 3;
        }
        return new MixFormat(rate, ch, bits, isFloat, mask);
    }

    unsafe void Run()
    {
        IMMDeviceEnumerator? en = null;
        IMMDevice? dev = null;
        IAudioClient? client = null;
        IAudioCaptureClient? cap = null;
        IntPtr pFmt = IntPtr.Zero;
        try
        {
            en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            int hr;
            if (string.IsNullOrEmpty(_deviceId)) hr = en.GetDefaultAudioEndpoint(0, 0, out dev);   // eRender, eConsole
            else hr = en.GetDevice(_deviceId, out dev);
            Marshal.ThrowExceptionForHR(hr);
            dev!.GetId(out var id);
            DeviceId = id;

            var iidClient = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iidClient, 23, IntPtr.Zero, out var o));
            client = (IAudioClient)o;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out pFmt));
            var fmt = ParseFormat(pFmt);
            Format = fmt;
            _log("回环抓取设备混音格式: " + fmt);

            var session = Guid.Empty;
            Marshal.ThrowExceptionForHR(client.Initialize(0, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, pFmt, ref session));
            var iidCap = typeof(IAudioCaptureClient).GUID;
            Marshal.ThrowExceptionForHR(client.GetService(ref iidCap, out var oc));
            cap = (IAudioCaptureClient)oc;
            Marshal.ThrowExceptionForHR(client.Start());
        }
        catch (Exception e)
        {
            _initError = e;
            _ready.Set();
            Cleanup(en, dev, client, cap, pFmt);
            return;
        }
        _ready.Set();

        var fmt2 = Format!;
        int ch = fmt2.Channels;
        var outBuf = new float[8192 * 2];
        int[] map = BuildChannelMap(fmt2);
        try
        {
            while (!_stop)
            {
                Thread.Sleep(5);
                for (;;)
                {
                    int hr = cap.GetNextPacketSize(out uint n);
                    if (hr != 0) { Stopped?.Invoke("GetNextPacketSize 失败 0x" + hr.ToString("X8")); return; }
                    if (n == 0) break;
                    hr = cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    if (hr != 0) break;   // 包含 AUDCLNT_S_BUFFER_EMPTY
                    if (frames > 0)
                    {
                        if (outBuf.Length < frames * 2) outBuf = new float[frames * 2];
                        if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(outBuf, 0, (int)frames * 2);
                        else ConvertToStereo(data, (int)frames, fmt2, map, outBuf);
                        FramesCaptured += frames;
                        DataAvailable?.Invoke(outBuf, (int)frames);
                    }
                    cap.ReleaseBuffer(frames);
                }
            }
        }
        catch (Exception e) { Stopped?.Invoke("抓取线程异常: " + e.Message); }
        finally
        {
            try { client?.Stop(); } catch { }
            Cleanup(en, dev, client, cap, pFmt);
        }
    }

    static void Cleanup(object? en, object? dev, object? client, object? cap, IntPtr pFmt)
    {
        if (pFmt != IntPtr.Zero) Marshal.FreeCoTaskMem(pFmt);
        foreach (var c in new[] { cap, client, dev, en }) { try { if (c != null) Marshal.ReleaseComObject(c); } catch { } }
    }

    // 声道映射：返回 [FL, FR, FC, BL, BR, SL, SR] 在帧内的下标（不存在为 -1）
    static int[] BuildChannelMap(MixFormat f)
    {
        var map = new[] { 0, 1, -1, -1, -1, -1, -1 };
        if (f.Channels <= 2) return map;
        int mask = f.ChannelMask;
        if (mask == 0) mask = f.Channels switch { 3 => 0x7, 4 => 0x33, 5 => 0x37, 6 => 0x3F, 8 => 0x63F, _ => 0x3 };
        // 按掩码位的顺序给每个声道编号：FL=0x1 FR=0x2 FC=0x4 LFE=0x8 BL=0x10 BR=0x20 ... SL=0x200 SR=0x400
        int idx = 0;
        for (int bit = 0; bit < 32 && idx < f.Channels; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            switch (1 << bit)
            {
                case 0x1: map[0] = idx; break;
                case 0x2: map[1] = idx; break;
                case 0x4: map[2] = idx; break;
                case 0x10: map[3] = idx; break;
                case 0x20: map[4] = idx; break;
                case 0x200: map[5] = idx; break;
                case 0x400: map[6] = idx; break;
            }
            idx++;
        }
        return map;
    }

    static unsafe float Sample(byte* p, int index, MixFormat f)
    {
        if (f.IsFloat && f.BitsPerSample == 32) return ((float*)p)[index];
        switch (f.BitsPerSample)
        {
            case 16: return ((short*)p)[index] / 32768f;
            case 24: { byte* q = p + index * 3; int v = (q[0] | (q[1] << 8) | (q[2] << 16)); if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000); return v / 8388608f; }
            case 32: return ((int*)p)[index] / 2147483648f;
            default: return 0f;
        }
    }

    static unsafe void ConvertToStereo(IntPtr data, int frames, MixFormat f, int[] map, float[] outBuf)
    {
        byte* p = (byte*)data;
        int ch = f.Channels;
        if (ch == 1)
        {
            for (int i = 0; i < frames; i++) { float s = Sample(p, i, f); outBuf[i * 2] = s; outBuf[i * 2 + 1] = s; }
        }
        else if (ch == 2)
        {
            for (int i = 0; i < frames; i++) { outBuf[i * 2] = Sample(p, i * 2, f); outBuf[i * 2 + 1] = Sample(p, i * 2 + 1, f); }
        }
        else
        {
            const float k = 0.7071f;
            for (int i = 0; i < frames; i++)
            {
                int b = i * ch;
                float l = map[0] >= 0 ? Sample(p, b + map[0], f) : 0, r = map[1] >= 0 ? Sample(p, b + map[1], f) : 0;
                if (map[2] >= 0) { float c = Sample(p, b + map[2], f) * k; l += c; r += c; }
                if (map[3] >= 0) l += Sample(p, b + map[3], f) * k;
                if (map[4] >= 0) r += Sample(p, b + map[4], f) * k;
                if (map[5] >= 0) l += Sample(p, b + map[5], f) * k;
                if (map[6] >= 0) r += Sample(p, b + map[6], f) * k;
                outBuf[i * 2] = Math.Clamp(l, -1f, 1f);
                outBuf[i * 2 + 1] = Math.Clamp(r, -1f, 1f);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(2000);
        _thread = null;
    }
}
