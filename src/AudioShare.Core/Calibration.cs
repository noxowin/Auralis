using System.Numerics;

namespace AudioShare.Core;

/// <summary>
/// 校准计划：在采集时间轴上给每台设备安排几次测试音（扫频"滴"声）。
/// 设备轮流响，间隔 1 秒，重复几轮取中位数，避免偶然的噪声干扰。
/// </summary>
public sealed class CalibrationPlan
{
    public CalibrationPlan(IReadOnlyList<string> deviceIds, long startFrame, int sampleRate, int rounds = 3, double spacingSeconds = 1.0)
    {
        if (deviceIds.Count == 0) throw new ArgumentException("至少需要一台设备");
        DeviceIds = deviceIds;
        StartFrame = startFrame;
        SampleRate = sampleRate;
        Rounds = rounds;
        SpacingFrames = (long)(spacingSeconds * sampleRate);
        Beep = MakeChirp(sampleRate);
    }

    public IReadOnlyList<string> DeviceIds { get; }
    public long StartFrame { get; }
    public int SampleRate { get; }
    public int Rounds { get; }
    public long SpacingFrames { get; }
    public float[] Beep { get; }

    public double SpacingSeconds => (double)SpacingFrames / SampleRate;

    public long ScheduledFrame(int device, int round) =>
        StartFrame + (round * DeviceIds.Count + device) * SpacingFrames;

    public long EndFrame => StartFrame + Rounds * DeviceIds.Count * SpacingFrames + Beep.Length;

    /// <summary>把这台设备在 [blockStart, blockStart+frames) 范围内要响的测试音写进 dst（交错多声道）。</summary>
    public void Render(string deviceId, long blockStart, Span<float> dst, int channels, int frames)
    {
        int device = -1;
        for (int i = 0; i < DeviceIds.Count; i++)
            if (DeviceIds[i] == deviceId) { device = i; break; }
        if (device < 0) return;

        long blockEnd = blockStart + frames;
        for (int r = 0; r < Rounds; r++)
        {
            long s = ScheduledFrame(device, r);
            long from = Math.Max(s, blockStart), to = Math.Min(s + Beep.Length, blockEnd);
            for (long f = from; f < to; f++)
            {
                float v = Beep[f - s];
                int o = (int)(f - blockStart) * channels;
                for (int c = 0; c < channels; c++) dst[o + c] = v;
            }
        }
    }

    /// <summary>60ms 线性扫频 800Hz→5kHz。扫频信号做互相关时峰值很尖，定位精度可达零点几毫秒。</summary>
    public static float[] MakeChirp(int sampleRate)
    {
        const double duration = 0.06, f0 = 800, f1 = 5000, fade = 0.003;
        int n = (int)(duration * sampleRate);
        var beep = new float[n];
        double k = (f1 - f0) / duration;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / sampleRate;
            double env = Math.Min(1, Math.Min(t / fade, (duration - t) / fade));
            beep[i] = (float)(0.6 * env * Math.Sin(2 * Math.PI * (f0 * t + 0.5 * k * t * t)));
        }
        return beep;
    }
}

public sealed record DeviceLatency(string DeviceId, double RelativeMs, int RoundsHeard);

public sealed record CalibrationResult(bool Success, string Message, IReadOnlyList<DeviceLatency> Devices, double Confidence);

/// <summary>分析麦克风录音：找出每台设备的测试音到达时间，算出设备之间的延迟差。</summary>
public static class CalibrationAnalyzer
{
    public static CalibrationResult Analyze(CalibrationPlan plan, float[] mic, int micRate)
    {
        var template = CalibrationPlan.MakeChirp(micRate);
        if (mic.Length < template.Length * 4)
            return Fail("录音太短");

        var ncc = NormalizedCrossCorrelation(mic, template);
        double max = 0;
        foreach (var v in ncc) if (v > max) max = v;
        if (max < 0.15)
            return Fail("麦克风没有听到测试音。请把耳机靠近麦克风，或者把耳机音量调大一些再试。");

        double threshold = Math.Max(0.1, 0.4 * max);

        // 第一个测试音一定是第 0 台设备第 0 轮：设备间延迟差小于半个间隔，其他设备的声音不可能更早到达
        int first = Array.FindIndex(ncc, v => v > threshold);
        int anchor = ArgMax(ncc, first, first + micRate / 50);

        int halfWindow = (int)(plan.SpacingSeconds * 0.45 * micRate);
        var devices = new List<DeviceLatency>();
        double minPeak = 1;

        for (int d = 0; d < plan.DeviceIds.Count; d++)
        {
            var offsets = new List<double>();
            for (int r = 0; r < plan.Rounds; r++)
            {
                double rel = (double)(plan.ScheduledFrame(d, r) - plan.ScheduledFrame(0, 0)) / plan.SampleRate;
                int expected = anchor + (int)Math.Round(rel * micRate);
                if (expected - halfWindow < 0 || expected + halfWindow >= ncc.Length) continue;

                int peak = ArgMax(ncc, expected - halfWindow, expected + halfWindow);
                if (ncc[peak] < threshold) continue;
                minPeak = Math.Min(minPeak, ncc[peak]);
                offsets.Add((peak - expected) * 1000.0 / micRate);
            }

            if (offsets.Count < Math.Min(2, plan.Rounds))
                return Fail($"没听清第 {d + 1} 台设备的测试音。请把它靠近麦克风，或者调大它的音量再试。", d);

            offsets.Sort();
            devices.Add(new DeviceLatency(plan.DeviceIds[d], offsets[offsets.Count / 2], offsets.Count));
        }

        return new CalibrationResult(true, "校准完成", devices, minPeak);

        CalibrationResult Fail(string message, int failedDevice = -1) =>
            new(false, message, failedDevice >= 0 ? new[] { new DeviceLatency(plan.DeviceIds[failedDevice], 0, 0) } : Array.Empty<DeviceLatency>(), 0);
    }

    /// <summary>
    /// 根据测量结果算出每台设备的新"延迟对齐"值：最慢的设备为 0，其他设备补足差值。
    /// relativeMs 是在当前延迟设置下测得的到达时间差，所以要先减掉当前已经加的延迟。
    /// </summary>
    public static Dictionary<string, double> SuggestDelays(IReadOnlyList<DeviceLatency> measured, IReadOnlyDictionary<string, double> currentDelays)
    {
        var inherent = measured.ToDictionary(m => m.DeviceId, m => m.RelativeMs - currentDelays.GetValueOrDefault(m.DeviceId));
        double slowest = inherent.Values.Max();
        return inherent.ToDictionary(kv => kv.Key, kv => Math.Round((slowest - kv.Value) / 5) * 5);
    }

    private static int ArgMax(double[] values, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(values.Length - 1, to);
        int best = from;
        for (int i = from + 1; i <= to; i++) if (values[i] > values[best]) best = i;
        return best;
    }

    /// <summary>归一化互相关：结果在 0~1 之间，和录音的音量大小无关。</summary>
    private static double[] NormalizedCrossCorrelation(float[] signal, float[] template)
    {
        int n = signal.Length, m = template.Length;
        int size = 1;
        while (size < n + m) size <<= 1;

        var a = new Complex[size];
        var b = new Complex[size];
        for (int i = 0; i < n; i++) a[i] = signal[i];
        for (int i = 0; i < m; i++) b[i] = template[i];
        Fft(a, false);
        Fft(b, false);
        for (int i = 0; i < size; i++) a[i] *= Complex.Conjugate(b[i]);
        Fft(a, true);

        double tplEnergy = 0;
        foreach (var v in template) tplEnergy += v * v;

        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + signal[i] * (double)signal[i];

        int count = n - m + 1;
        var result = new double[count];
        for (int i = 0; i < count; i++)
        {
            double energy = prefix[i + m] - prefix[i];
            double denom = Math.Sqrt(energy * tplEnergy) + 1e-9;
            result[i] = Math.Max(0, a[i].Real / size / denom);
        }
        return result;
    }

    private static void Fft(Complex[] data, bool inverse)
    {
        int n = data.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = data[i + k];
                    var v = data[i + k + len / 2] * w;
                    data[i + k] = u + v;
                    data[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }
}
