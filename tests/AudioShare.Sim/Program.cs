using AudioShare.Core;
using NAudio.Wave;

// 离线模拟：采集端 48kHz，输出端时钟有偏差，检查缓冲水位是否稳定、是否出现断音，
// 以及"缓冲不够"和"系统没声音"两种读空能否被正确区分。

/// <param name="outBlockMs">输出设备每次取多少毫秒（蓝牙设备通常取得更多、更不规律）</param>
/// <param name="silenceFrom">从第几秒开始采集端停止送数据（模拟系统暂停播放），-1 表示一直有声音</param>
static (int underruns, int crackles) Run(string name, int outRate, double clockPpm, int targetMs, double minutes,
    int outBlockMs = 10, double silenceFrom = -1)
{
    const int inRate = 48000, ch = 2;
    var ring = new FloatRingBuffer(inRate * ch * 2);
    double now = 0; // 虚拟时间（秒）
    var rs = new AdaptiveResampler(ring, ch, inRate, WaveFormat.CreateIeeeFloatWaveFormat(outRate, ch))
    {
        TargetLatencyMs = targetMs,
        NowMs = () => (long)(now * 1000),
    };
    var rnd = new Random(1);

    double tIn = 0, tOut = 0.005;
    double inPeriod = 0.010;
    int outBlock = outRate * outBlockMs / 1000;
    double outPeriod = outBlock / (outRate * (1 + clockPpm / 1e6));
    long inSample = 0, inBlocks = 0;
    var inBuf = new float[480 * ch];
    var outBuf = new float[outBlock * ch];
    int underruns = 0; bool wasPlaying = false;
    double minMs = double.MaxValue, maxMs = 0;
    double end = minutes * 60;

    while (tOut < end)
    {
        if (tIn <= tOut)
        {
            now = tIn;
            if (silenceFrom < 0 || tIn < silenceFrom)
            {
                for (int i = 0; i < 480; i++, inSample++)
                {
                    float v = (float)Math.Sin(2 * Math.PI * 1000 * inSample / inRate) * 0.5f;
                    inBuf[i * 2] = v; inBuf[i * 2 + 1] = v;
                }
                ring.Write(inBuf);
                ring.LastWriteMs = (long)(tIn * 1000);
            }
            inBlocks++;
            tIn = inBlocks * inPeriod + (rnd.NextDouble() - 0.5) * 0.004; // 采集抖动 ±2ms（不累积）
        }
        else
        {
            now = tOut;
            rs.Read(outBuf, 0, outBuf.Length);
            if (wasPlaying && rs.IsPriming) underruns++;
            wasPlaying = !rs.IsPriming;
            if (tOut > 30 && silenceFrom < 0)
            {
                minMs = Math.Min(minMs, rs.BufferedMs);
                maxMs = Math.Max(maxMs, rs.BufferedMs);
            }
            tOut += outPeriod;
        }
    }
    string range = silenceFrom < 0 ? $"稳定后缓冲 {minMs,6:F1}~{maxMs,6:F1} ms" : "";
    Console.WriteLine($"{name,-30} 断音 {underruns,3} 次  杂音 {rs.CrackleCount,3} 次  {range} (目标 {targetMs}ms)");
    return (underruns, rs.CrackleCount);
}

int bad = 0;
void Expect(bool ok, string what)
{
    if (!ok) { bad++; Console.WriteLine($"  ✗ {what}"); }
}

// 正常情况：不应断音
foreach (var (n, rate, ppm, target) in new[]
{
    ("48k 设备, 时钟 +300ppm", 48000, 300.0, 80),
    ("48k 设备, 时钟 -500ppm", 48000, -500.0, 80),
    ("44.1k 蓝牙, 时钟 +1000ppm", 44100, 1000.0, 80),
    ("96k 声卡, 时钟 -200ppm, 40ms", 96000, -200.0, 40),
    ("44.1k, 无偏差, 250ms", 44100, 0.0, 250),
})
{
    var r = Run(n, rate, ppm, target, 10);
    Expect(r.underruns == 0 && r.crackles == 0, $"{n} 不应断音");
}

// 蓝牙一次取 40ms、缓冲只给 20ms：必然断续，应该被识别为杂音
var starved = Run("蓝牙大块读取, 缓冲太小", 44100, 0, 20, 1, outBlockMs: 40);
Expect(starved.crackles > 0, "缓冲太小时应检测到杂音");

// 同样的设备给足缓冲：不应有杂音
var enough = Run("蓝牙大块读取, 缓冲足够", 44100, 0, 80, 3, outBlockMs: 40);
Expect(enough.crackles == 0, "缓冲足够时不应有杂音");

// 系统暂停播放：会读空，但不应算作杂音
var silence = Run("10 秒后系统暂停播放", 48000, 0, 40, 1, silenceFrom: 10);
Expect(silence.crackles == 0, "系统没声音时不应算作杂音");

// 自动校准：模拟麦克风录音（不同设备不同延迟、回声、底噪、音量差异），检查能否算准
void Calibrate(string name, int micRate, double[] latenciesMs, double[] gains, double noise, double[] currentDelays)
{
    var ids = latenciesMs.Select((_, i) => $"dev{i}").ToArray();
    var plan = new CalibrationPlan(ids, startFrame: 12345, sampleRate: 48000);
    double c = 0.37; // 未知的公共偏移（采集到麦克风开录之间的时间）
    var rnd = new Random(7);
    var mic = new float[(int)((plan.EndFrame - plan.StartFrame) / 48000.0 * micRate + 1.5 * micRate)];
    for (int i = 0; i < mic.Length; i++)
    {
        // 高斯底噪
        double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
        mic[i] = (float)(noise * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
    }
    for (int d = 0; d < ids.Length; d++)
        for (int r = 0; r < plan.Rounds; r++)
        {
            double arrive = (plan.ScheduledFrame(d, r) - plan.StartFrame) / 48000.0 + (latenciesMs[d] + currentDelays[d]) / 1000 + c;
            foreach (var (delay, amp) in new[] { (0.0, 1.0), (0.008, 0.35), (0.021, 0.15) }) // 直达声 + 两次反射
                for (int i = 0; i < plan.Beep.Length; i++)
                {
                    double t = arrive + delay + (double)i / 48000;
                    int idx = (int)(t * micRate);
                    if (idx < mic.Length) mic[idx] += (float)(plan.Beep[i] * gains[d] * amp * micRate / 48000.0);
                }
        }

    var result = CalibrationAnalyzer.Analyze(plan, mic, micRate);
    if (!result.Success) { Expect(false, $"{name}: {result.Message}"); return; }

    var current = ids.Select((id, i) => (id, currentDelays[i])).ToDictionary(x => x.id, x => x.Item2);
    var suggested = CalibrationAnalyzer.SuggestDelays(result.Devices, current);
    double worst = 0;
    var parts = new List<string>();
    for (int d = 0; d < ids.Length; d++)
    {
        double expectRel = (latenciesMs[d] + currentDelays[d]) - (latenciesMs[0] + currentDelays[0]);
        worst = Math.Max(worst, Math.Abs(result.Devices[d].RelativeMs - expectRel));
        // 对齐后每台设备的总延迟应该相同
        parts.Add($"{latenciesMs[d] + suggested[ids[d]]:0}");
    }
    double spread = ids.Select((id, d) => latenciesMs[d] + suggested[id]).Max() - ids.Select((id, d) => latenciesMs[d] + suggested[id]).Min();
    Console.WriteLine($"{name,-30} 测量误差 {worst:F2} ms  对齐后总延迟 [{string.Join(", ", parts)}] ms  置信度 {result.Confidence:F2}");
    Expect(worst < 2, $"{name}: 测量误差应小于 2ms");
    Expect(spread <= 5, $"{name}: 对齐后差距应不超过 5ms");
}

Calibrate("有线 + 蓝牙(200ms)", 48000, new[] { 15.0, 215.0 }, new[] { 1.0, 0.3 }, 0.02, new[] { 0.0, 0.0 });
Calibrate("蓝牙在前, 已有延迟设置", 44100, new[] { 230.0, 12.0 }, new[] { 0.5, 1.0 }, 0.02, new[] { 0.0, 150.0 });
Calibrate("三台设备, 16k 麦克风, 噪声大", 16000, new[] { 40.0, 180.0, 5.0 }, new[] { 0.2, 0.15, 0.3 }, 0.05, new[] { 0.0, 0.0, 0.0 });

// 麦克风什么都没听到：应该失败并给出提示，而不是给出错误结果
{
    var plan = new CalibrationPlan(new[] { "a", "b" }, 0, 48000);
    var rnd = new Random(3);
    var mic = Enumerable.Range(0, 48000 * 8).Select(_ => (float)(rnd.NextDouble() - 0.5) * 0.05f).ToArray();
    var r = CalibrationAnalyzer.Analyze(plan, mic, 48000);
    Console.WriteLine($"{"麦克风没听到测试音",-30} 结果：{(r.Success ? "成功（错误）" : "失败：" + r.Message)}");
    Expect(!r.Success, "没听到测试音时应该报告失败");
}

// 音效：低音 +6dB 时 80Hz 应变大约 2 倍，5kHz 基本不变；音量 200% 时输出不超过 1.0
double ToneGain(double freq, EqSettings eq, float volume = 1f)
{
    const int rate = 48000, block = 4800; // 每次写入、读出 100ms
    var ring = new FloatRingBuffer(rate * 2);
    var rs = new AdaptiveResampler(ring, 2, rate, WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { TargetLatencyMs = 100, Volume = volume };
    rs.SetEq(eq);
    var input = new float[block * 2];
    var output = new float[block * 2];
    double max = 0;
    long n = 0;
    for (int b = 0; b < 30; b++)
    {
        for (int i = 0; i < block; i++, n++) input[2 * i] = input[2 * i + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * freq * n / rate));
        ring.Write(input);
        rs.Read(output, 0, output.Length);
        if (b >= 20) max = Math.Max(max, output.Max(Math.Abs)); // 最后 1 秒，避开蓄水和滤波器起振
    }
    return max / 0.25;
}

double bass80 = ToneGain(80, new EqSettings(BassDb: 6));
double bass5k = ToneGain(5000, new EqSettings(BassDb: 6));
double treble10k = ToneGain(10000, new EqSettings(TrebleDb: -6));
double loud = ToneGain(50, new EqSettings(BassDb: 12), volume: 2f) * 0.25;
Console.WriteLine($"{"音效",-30} 低音+6dB: 80Hz ×{bass80:F2}, 5kHz ×{bass5k:F2}；高音-6dB: 10kHz ×{treble10k:F2}；200%+低音12dB 峰值 {loud:F3}");
Expect(bass80 > 1.6 && bass80 < 2.1, "低音 +6dB 时 80Hz 应放大约 2 倍");
Expect(Math.Abs(bass5k - 1) < 0.05, "低音调节不应影响 5kHz");
Expect(treble10k > 0.45 && treble10k < 0.65, "高音 -6dB 时 10kHz 应约减半");
Expect(loud <= 1.0, "软限幅后峰值不应超过 1.0");

Console.WriteLine(bad == 0 ? "PASS" : "FAIL");
return bad == 0 ? 0 : 1;
