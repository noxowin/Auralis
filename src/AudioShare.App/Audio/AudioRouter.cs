using AudioShare.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare.App.Audio;

/// <summary>
/// 用 WASAPI loopback 抓取某个输出设备正在播放的声音（即"系统声音"），
/// 再把同一份数据分发给多个输出设备。
/// </summary>
public sealed class AudioRouter : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private volatile OutputChannel[] _outputs = Array.Empty<OutputChannel>();
    private float[] _floatBuffer = Array.Empty<float>();
    private readonly object _gate = new();
    private volatile float _sourcePeak;

    public bool IsRunning => _capture != null;

    public string? SourceDeviceId { get; private set; }

    public WaveFormat? CaptureFormat { get; private set; }

    /// <summary>源设备上正在播放的声音电平。</summary>
    public float SourcePeak => _sourcePeak;

    /// <summary>采集意外停止（例如源设备被拔出），在后台线程触发。</summary>
    public event Action<Exception?>? CaptureStopped;

    /// <summary>某个输出意外停止，在后台线程触发。</summary>
    public event Action<string, Exception?>? OutputStopped;

    public IReadOnlyList<OutputChannel> Outputs => _outputs;

    public void Start(MMDevice source)
    {
        Stop();
        var capture = new WasapiLoopbackCapture(source);
        CaptureFormat = capture.WaveFormat;
        SourceDeviceId = source.ID;
        capture.DataAvailable += OnDataAvailable;
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null) CaptureStopped?.Invoke(e.Exception);
        };
        _capture = capture;
        capture.StartRecording();
    }

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture != null)
        {
            capture.DataAvailable -= OnDataAvailable;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }

        OutputChannel[] old;
        lock (_gate)
        {
            old = _outputs;
            _outputs = Array.Empty<OutputChannel>();
        }
        foreach (var o in old) o.Dispose();
        SourceDeviceId = null;
        CaptureFormat = null;
        _sourcePeak = 0;
    }

    public OutputChannel AddOutput(MMDevice device, int targetLatencyMs, float volume)
    {
        if (CaptureFormat == null) throw new InvalidOperationException("请先开始采集");
        RemoveOutput(device.ID);

        var channel = new OutputChannel(device, CaptureFormat, targetLatencyMs, volume);
        channel.Stopped += (c, ex) =>
        {
            if (ex != null) OutputStopped?.Invoke(c.DeviceId, ex);
        };

        lock (_gate)
        {
            _outputs = _outputs.Append(channel).ToArray();
        }
        return channel;
    }

    public void RemoveOutput(string deviceId)
    {
        OutputChannel? removed;
        lock (_gate)
        {
            removed = _outputs.FirstOrDefault(o => o.DeviceId == deviceId);
            if (removed == null) return;
            _outputs = _outputs.Where(o => o != removed).ToArray();
        }
        removed.Dispose();
    }

    public OutputChannel? Find(string deviceId) => _outputs.FirstOrDefault(o => o.DeviceId == deviceId);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var format = CaptureFormat;
        if (format == null || e.BytesRecorded == 0) return;

        int samples = SampleConverter.ToFloat(format, e.Buffer, e.BytesRecorded, ref _floatBuffer);
        int channels = format.Channels;
        int frames = samples / channels;

        float peak = 0;
        for (int i = 0; i < samples; i++)
        {
            float a = Math.Abs(_floatBuffer[i]);
            if (a > peak) peak = a;
        }
        _sourcePeak = peak;

        foreach (var output in _outputs)
            output.Push(_floatBuffer, channels, frames);
    }

    public void Dispose() => Stop();
}
