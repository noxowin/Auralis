using AudioShare.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioShare.App.Audio;

/// <summary>
/// 自动校准：让每台设备轮流放测试音，用麦克风录下来，算出设备之间的延迟差。
/// </summary>
public static class Calibrator
{
    public static async Task<CalibrationResult> RunAsync(
        AudioRouter router, MMDevice sourceDevice, MMDevice microphone, IReadOnlyList<string> deviceIds,
        IProgress<string> progress, CancellationToken cancellation)
    {
        var format = router.CaptureFormat ?? throw new InvalidOperationException("请先开始共享");

        // 系统没在放声音时 loopback 不送数据，测试音也就发不出去。
        // 往来源设备放一段静音，让采集时间轴一直走。来源设备在对齐模式下是静音的中转设备，听不到。
        using var keepAlive = new WasapiOut(sourceDevice, AudioClientShareMode.Shared, true, 50);
        keepAlive.Init(new SilenceProvider(sourceDevice.AudioClient.MixFormat));
        keepAlive.Play();

        using var mic = new WasapiCapture(microphone, true, 20);
        var micFormat = mic.WaveFormat;
        var recording = new List<float>(micFormat.SampleRate * 15);
        float[] convert = Array.Empty<float>();
        mic.DataAvailable += (_, e) =>
        {
            int samples = SampleConverter.ToFloat(micFormat, e.Buffer, e.BytesRecorded, ref convert);
            int ch = micFormat.Channels;
            lock (recording)
            {
                for (int i = 0; i + ch <= samples; i += ch)
                {
                    float sum = 0;
                    for (int c = 0; c < ch; c++) sum += convert[i + c];
                    recording.Add(sum / ch);
                }
            }
        };

        CalibrationPlan? plan = null;
        try
        {
            mic.StartRecording();
            progress.Report("准备中，请保持安静…");
            await Task.Delay(800, cancellation);

            plan = new CalibrationPlan(deviceIds, router.SourceFrames + format.SampleRate / 2, format.SampleRate);
            router.SetCalibration(plan);
            progress.Report("正在播放测试音，请保持安静…");

            // 等所有测试音播完，再多等一会儿，让最慢的设备（蓝牙）也放完
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (router.SourceFrames < plan.EndFrame)
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("系统声音没有数据，测试音发不出去");
                await Task.Delay(100, cancellation);
            }
            await Task.Delay(1200, cancellation);
        }
        finally
        {
            router.SetCalibration(null);
            try { mic.StopRecording(); } catch { }
            keepAlive.Stop();
        }

        progress.Report("正在分析…");
        float[] samplesCopy;
        lock (recording) samplesCopy = recording.ToArray();
        return await Task.Run(() => CalibrationAnalyzer.Analyze(plan!, samplesCopy, micFormat.SampleRate), cancellation);
    }
}
