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
    private long _sourceFrames;
    private volatile CalibrationPlan? _calibration;
    private float[] _calibrationBuffer = Array.Empty<float>();

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

    /// <summary>从开始采集到现在送出的总帧数，校准时用作时间轴。</summary>
    public long SourceFrames => Interlocked.Read(ref _sourceFrames);

    /// <summary>
    /// 校准期间：所有输出只播放静音和测试音，测试音按计划插在指定的帧位置上。
    /// 因为所有设备共用同一条采集时间轴，测得的到达时间差就是设备之间的延迟差。
    /// </summary>
    public void SetCalibration(CalibrationPlan? plan) => _calibration = plan;

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

        long blockStart = Interlocked.Add(ref _sourceFrames, frames) - frames;
        var plan = _calibration;
        if (plan == null)
        {
            foreach (var output in _outputs)
                output.Push(_floatBuffer, channels, frames);
            return;
        }

        if (_calibrationBuffer.Length < samples) _calibrationBuffer = new float[samples];
        foreach (var output in _outputs)
        {
            Array.Clear(_calibrationBuffer, 0, samples);
            plan.Render(output.DeviceId, blockStart, _calibrationBuffer, channels, frames);
            output.Push(_calibrationBuffer, channels, frames);
        }
    }

    public void Dispose() => Stop();
}
