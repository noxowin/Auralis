using AudioShare.Core;
using NAudio.Wave;

// 离线模拟：采集端 48kHz，输出端时钟有偏差，检查缓冲水位是否稳定、是否出现断音。
static int Run(string name, int outRate, double clockPpm, int targetMs, double minutes)
{
    const int inRate = 48000, ch = 2;
    var ring = new FloatRingBuffer(inRate * ch * 2);
    var rs = new AdaptiveResampler(ring, ch, inRate, WaveFormat.CreateIeeeFloatWaveFormat(outRate, ch)) { TargetLatencyMs = targetMs };
    var rnd = new Random(1);

    double tIn = 0, tOut = 0.005;           // 秒
    double inPeriod = 0.010;                 // 采集每 10ms 送一块
    int outBlock = outRate / 100;            // 播放每次取 10ms
    double outPeriod = outBlock / (outRate * (1 + clockPpm / 1e6));
    long inSample = 0, inBlocks = 0;
    var inBuf = new float[480 * ch];
    var outBuf = new float[outBlock * ch];
    int underruns = 0; bool wasPlaying = false;
    double minMs = double.MaxValue, maxMs = 0, lastMs = 0;
    double end = minutes * 60;

    while (tOut < end)
    {
        if (tIn <= tOut)
        {
            for (int i = 0; i < 480; i++, inSample++)
            {
                float v = (float)Math.Sin(2 * Math.PI * 1000 * inSample / inRate) * 0.5f;
                inBuf[i * 2] = v; inBuf[i * 2 + 1] = v;
            }
            ring.Write(inBuf);
            inBlocks++;
            tIn = inBlocks * inPeriod + (rnd.NextDouble() - 0.5) * 0.004; // 采集抖动 ±2ms（不累积）
        }
        else
        {
            rs.Read(outBuf, 0, outBuf.Length);
            if (wasPlaying && rs.IsPriming) { underruns++; if (Environment.GetEnvironmentVariable("TRACE")!=null) Console.WriteLine($"  underrun t={tOut:F2} buf={rs.BufferedMs:F1}"); }
            if (Environment.GetEnvironmentVariable("TRACE")!=null && ((int)(tOut*100))%200==0) Console.WriteLine($"  t={tOut:F2} buf={rs.BufferedMs:F1} adj={rs.CurrentAdjustPpm:F0}");
            wasPlaying = !rs.IsPriming;
            if (tOut > 30) { lastMs = rs.BufferedMs; minMs = Math.Min(minMs, lastMs); maxMs = Math.Max(maxMs, lastMs); }
            tOut += outPeriod;
        }
    }
    Console.WriteLine($"{name,-28} 断音 {underruns,3} 次  稳定后缓冲 {minMs,6:F1}~{maxMs,6:F1} ms (目标 {targetMs}ms)  修正 {rs.CurrentAdjustPpm,7:F0} ppm");
    return underruns;
}

int bad = 0;
bad += Run("48k 设备, 时钟 +300ppm", 48000, 300, 80, 10);
bad += Run("48k 设备, 时钟 -500ppm", 48000, -500, 80, 10);
bad += Run("44.1k 蓝牙, 时钟 +1000ppm", 44100, 1000, 80, 10);
bad += Run("96k 声卡, 时钟 -200ppm, 40ms", 96000, -200, 40, 10);
bad += Run("44.1k, 无偏差, 250ms", 44100, 0, 250, 10);
Console.WriteLine(bad == 0 ? "PASS" : "FAIL");
return bad == 0 ? 0 : 1;
