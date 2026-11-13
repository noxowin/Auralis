using AudioShare.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare.App.Audio;

/// <summary>一个输出设备：独立的缓冲区 + 自适应重采样 + WASAPI 播放。</summary>
public sealed class OutputChannel : IDisposable
{
    // WASAPI 自身的设备缓冲，越小越跟手，太小容易爆音
    private const int DeviceBufferMs = 30;

    private readonly WasapiOut _player;
    private readonly FloatRingBuffer _ring;
    private readonly AdaptiveResampler _resampler;
    private readonly int _channels;
    private float[] _mapBuffer = Array.Empty<float>();
    private bool _disposed;

    public OutputChannel(MMDevice device, WaveFormat captureFormat, int targetLatencyMs, float volume)
    {
        DeviceId = device.ID;
        var mixFormat = device.AudioClient.MixFormat;
        _channels = mixFormat.Channels;
        OutputFormat = mixFormat;

        // 最多缓存 3 秒，足够覆盖最大延迟补偿
        _ring = new FloatRingBuffer(captureFormat.SampleRate * _channels * 3);
        _resampler = new AdaptiveResampler(_ring, _channels, captureFormat.SampleRate, mixFormat)
        {
            TargetLatencyMs = targetLatencyMs,
            Volume = volume,
        };

        _player = new WasapiOut(device, AudioClientShareMode.Shared, true, DeviceBufferMs);
        _player.PlaybackStopped += (_, e) => Stopped?.Invoke(this, e.Exception);
        _player.Init(new FloatWaveProvider(_resampler));
        _player.Play();
    }

    public string DeviceId { get; }

    public WaveFormat OutputFormat { get; }

    /// <summary>播放意外停止（例如设备被拔出），在后台线程触发。</summary>
    public event Action<OutputChannel, Exception?>? Stopped;

    public float Peak => _resampler.Peak;

    public double BufferedMs => _resampler.BufferedMs;

    public bool IsWaiting => _resampler.IsPriming;

    /// <summary>因为缓冲不够出现断续（杂音）的累计次数。</summary>
    public int CrackleCount => _resampler.CrackleCount;

    public void SetEq(EqSettings settings) => _resampler.SetEq(settings);

    /// <summary>左右声道合并：只戴一只耳机时也能听到完整内容。</summary>
    public bool Mono { get; set; }

    public int TargetLatencyMs
    {
        get => _resampler.TargetLatencyMs;
        set => _resampler.TargetLatencyMs = value;
    }

    public float Volume
    {
        get => _resampler.Volume;
        set => _resampler.Volume = value;
    }

    /// <summary>由采集线程调用，送入一块交错 float 样本。</summary>
    public void Push(float[] source, int sourceChannels, int frames)
    {
        int needed = frames * _channels;
        if (_mapBuffer.Length < needed) _mapBuffer = new float[needed * 2];
        ChannelMapper.Map(source, sourceChannels, _mapBuffer, _channels, frames);
        if (Mono && _channels >= 2)
        {
            for (int f = 0; f < frames; f++)
            {
                int o = f * _channels;
                float m = (_mapBuffer[o] + _mapBuffer[o + 1]) * 0.5f;
                _mapBuffer[o] = m;
                _mapBuffer[o + 1] = m;
            }
        }
        _ring.Write(_mapBuffer.AsSpan(0, needed));
        _ring.LastWriteMs = Environment.TickCount64;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _player.Stop(); } catch { /* 设备可能已经不存在 */ }
        _player.Dispose();
    }
}
