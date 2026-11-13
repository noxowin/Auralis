namespace AudioShare.Core;

/// <summary>三段音效：低音（100Hz 低架）、人声（1.5kHz 峰值）、高音（8kHz 高架），各 ±12 dB。</summary>
public sealed record EqSettings(double BassDb = 0, double MidDb = 0, double TrebleDb = 0)
{
    public bool IsFlat => Math.Abs(BassDb) < 0.05 && Math.Abs(MidDb) < 0.05 && Math.Abs(TrebleDb) < 0.05;
}

/// <summary>
/// 每台设备一个均衡器，在输出采样率上处理交错多声道数据。
/// 参数修改时整体替换滤波器组（播放线程下一块开始生效），不需要加锁。
/// </summary>
public sealed class Equalizer
{
    private readonly int _channels;
    private readonly int _sampleRate;
    private volatile Biquad[][]? _filters; // [channel][stage]，null 表示不处理

    public Equalizer(int channels, int sampleRate)
    {
        _channels = channels;
        _sampleRate = sampleRate;
    }

    public void Update(EqSettings settings)
    {
        if (settings.IsFlat)
        {
            _filters = null;
            return;
        }

        var filters = new Biquad[_channels][];
        for (int c = 0; c < _channels; c++)
        {
            filters[c] = new[]
            {
                Biquad.LowShelf(_sampleRate, 100, settings.BassDb),
                Biquad.Peaking(_sampleRate, 1500, 0.8, settings.MidDb),
                Biquad.HighShelf(_sampleRate, 8000, settings.TrebleDb),
            };
        }
        _filters = filters;
    }

    public void Process(Span<float> interleaved)
    {
        var filters = _filters;
        if (filters == null) return;

        for (int i = 0; i < interleaved.Length; i++)
        {
            var stages = filters[i % _channels];
            float v = interleaved[i];
            foreach (var f in stages) v = f.Process(v);
            interleaved[i] = v;
        }
    }

    /// <summary>
    /// 软限幅：0.9 以下原样输出，超过的部分平滑压缩到 1.0 以内。
    /// 音量调到 100% 以上或加了低音时，防止削波产生的爆音。
    /// </summary>
    public static float SoftClip(float x)
    {
        const float knee = 0.9f, range = 1f - knee;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float y = knee + range * MathF.Tanh((a - knee) / range);
        return x < 0 ? -y : y;
    }
}

/// <summary>二阶 IIR 滤波器，系数按 RBJ Audio EQ Cookbook 计算。</summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public float Process(float x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return (float)y;
    }

    public static Biquad Peaking(int rate, double freq, double q, double gainDb)
    {
        double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * freq / rate;
        double alpha = Math.Sin(w) / (2 * q), cos = Math.Cos(w);
        return new Biquad(1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
    }

    public static Biquad LowShelf(int rate, double freq, double gainDb) => Shelf(rate, freq, gainDb, low: true);

    public static Biquad HighShelf(int rate, double freq, double gainDb) => Shelf(rate, freq, gainDb, low: false);

    private static Biquad Shelf(int rate, double freq, double gainDb, bool low)
    {
        double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * freq / rate;
        double cos = Math.Cos(w), alpha = Math.Sin(w) / 2 * Math.Sqrt(2); // 斜率 S = 1
        double sq = 2 * Math.Sqrt(a) * alpha;
        return low
            ? new Biquad(
                a * ((a + 1) - (a - 1) * cos + sq), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - sq),
                (a + 1) + (a - 1) * cos + sq, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - sq)
            : new Biquad(
                a * ((a + 1) + (a - 1) * cos + sq), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - sq),
                (a + 1) - (a - 1) * cos + sq, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - sq);
    }
}
