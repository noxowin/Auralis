using NAudio.Wave;

namespace AudioShare.Core;

/// <summary>
/// 从环形缓冲区读取采集到的音频，转换到输出设备的采样率，并持续微调重采样比例，
/// 让缓冲区里的数据量稳定在目标延迟附近。
///
/// 每个声卡都有自己的晶振，48000Hz 的设备实际可能是 48003Hz 或 47995Hz。
/// 如果不处理，几分钟后缓冲区就会被读空（爆音）或越积越多（延迟变大、设备之间不同步）。
/// 这里用一个 PI 控制器根据缓冲水位把播放速度调快/调慢最多 ±0.5%，人耳听不出来。
/// </summary>
public sealed class AdaptiveResampler : ISampleProvider
{
    private const double MaxAdjust = 0.005;
    private const double Kp = 0.05;
    private const double Ki = 0.01;
    private const double HardResyncSeconds = 0.05;

    private readonly FloatRingBuffer _ring;
    private readonly int _channels;
    private readonly int _inRate;
    private readonly double _baseRatio;

    // 本地输入窗口：保留若干帧以便做三次插值
    private float[] _local;
    private int _localFrames;
    private double _pos;

    private bool _priming = true;
    private double _smoothedFill = -1;
    private double _integral;

    private volatile int _targetLatencyMs = 80;
    private volatile float _volume = 1f;
    private volatile float _peak;

    public AdaptiveResampler(FloatRingBuffer ring, int channels, int inRate, WaveFormat outputFormat)
    {
        if (outputFormat.Channels != channels)
            throw new ArgumentException("输入和输出声道数必须一致（先用 ChannelMapper 转换）");

        _ring = ring;
        _channels = channels;
        _inRate = inRate;
        _baseRatio = (double)inRate / outputFormat.SampleRate;
        _local = new float[4096 * channels];
        WaveFormat = SampleConverter.IsFloat32(outputFormat)
            ? outputFormat
            : WaveFormat.CreateIeeeFloatWaveFormat(outputFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>目标缓冲延迟（毫秒）。增大它可以让这台设备"晚一点"出声，用于和蓝牙等高延迟设备对齐。</summary>
    public int TargetLatencyMs
    {
        get => _targetLatencyMs;
        set => _targetLatencyMs = Math.Clamp(value, 10, 2000);
    }

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 2f);
    }

    /// <summary>最近一次输出块的峰值，用于界面电平表。</summary>
    public float Peak => _peak;

    /// <summary>当前速度修正量（百万分之一），用于诊断。</summary>
    public double CurrentAdjustPpm { get; private set; }

    /// <summary>当前缓冲的输入数据量（毫秒）。</summary>
    public double BufferedMs => BufferedFrames() * 1000.0 / _inRate;

    public bool IsPriming => _priming;

    private double BufferedFrames() => (double)_ring.Count / _channels + Math.Max(0, _localFrames - _pos);

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        var outSpan = buffer.AsSpan(offset, frames * _channels);
        double targetFrames = _targetLatencyMs * _inRate / 1000.0;
        double fill = BufferedFrames();

        if (_priming)
        {
            if (fill < targetFrames)
            {
                outSpan.Clear();
                _peak = 0;
                return count;
            }
            _priming = false;
            _smoothedFill = fill;
            _integral = 0;
        }

        _smoothedFill = _smoothedFill < 0 ? fill : _smoothedFill * 0.9 + fill * 0.1;
        double errFrames = _smoothedFill - targetFrames;
        double errSec = errFrames / _inRate;

        if (errSec > HardResyncSeconds)
        {
            // 积压太多（例如刚把延迟调小）：直接丢掉多余部分
            DiscardFrames((int)(fill - targetFrames));
            _smoothedFill = targetFrames;
            _integral = 0;
            errSec = 0;
        }
        else if (errSec < -HardResyncSeconds)
        {
            // 数据不够（例如刚把延迟调大）：重新蓄水，以静音补足
            _priming = true;
            outSpan.Clear();
            _peak = 0;
            return count;
        }

        double dt = (double)frames / WaveFormat.SampleRate;
        _integral = Math.Clamp(_integral + errSec * dt, -MaxAdjust / Ki, MaxAdjust / Ki);
        double adjust = Math.Clamp(Kp * errSec + Ki * _integral, -MaxAdjust, MaxAdjust);
        CurrentAdjustPpm = adjust * 1e6;
        double ratio = _baseRatio * (1 + adjust);

        float vol = _volume;
        float peak = 0;
        int written = 0;

        for (; written < frames; written++)
        {
            int ip = (int)_pos;
            if (ip + 2 >= _localFrames && !Refill(ip))
            {
                // 采集端没有数据（系统静音时 WASAPI loopback 不会送数据）
                _priming = true;
                break;
            }
            ip = (int)_pos;
            float t = (float)(_pos - ip);

            int o = written * _channels;
            for (int c = 0; c < _channels; c++)
            {
                float y0 = Sample(ip - 1, c), y1 = Sample(ip, c), y2 = Sample(ip + 1, c), y3 = Sample(ip + 2, c);
                float v = CatmullRom(y0, y1, y2, y3, t) * vol;
                outSpan[o + c] = v;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            _pos += ratio;
        }

        if (written < frames) outSpan[(written * _channels)..].Clear();
        _peak = peak;
        return count;
    }

    private float Sample(int frame, int channel)
    {
        if (frame < 0) frame = 0;
        return _local[frame * _channels + channel];
    }

    /// <summary>把已消费的帧移出本地窗口，再从环形缓冲读入新数据。</summary>
    private bool Refill(int ip)
    {
        int keepFrom = Math.Max(0, ip - 1);
        int keep = _localFrames - keepFrom;
        if (keepFrom > 0)
        {
            Array.Copy(_local, keepFrom * _channels, _local, 0, keep * _channels);
            _localFrames = keep;
            _pos -= keepFrom;
        }

        int space = _local.Length / _channels - _localFrames;
        int got = _ring.Read(_local.AsSpan(_localFrames * _channels, space * _channels)) / _channels;
        _localFrames += got;
        return (int)_pos + 2 < _localFrames;
    }

    private void DiscardFrames(int frames)
    {
        if (frames <= 0) return;
        int localAvail = Math.Max(0, _localFrames - (int)_pos - 3);
        int fromLocal = Math.Min(frames, localAvail);
        _pos += fromLocal;
        _ring.Discard((frames - fromLocal) * _channels);
    }

    public void Reset()
    {
        _ring.Clear();
        _localFrames = 0;
        _pos = 0;
        _priming = true;
        _smoothedFill = -1;
        _integral = 0;
    }

    private static float CatmullRom(float y0, float y1, float y2, float y3, float t)
    {
        float a = -0.5f * y0 + 1.5f * y1 - 1.5f * y2 + 0.5f * y3;
        float b = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        float c = -0.5f * y0 + 0.5f * y2;
        return ((a * t + b) * t + c) * t + y1;
    }
}
