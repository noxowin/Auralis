using System.IO;
using System.Text.Json;

namespace AudioShare.App;

public sealed class DeviceSettings
{
    public bool Enabled { get; set; }
    public double Volume { get; set; } = 100;
    public double DelayMs { get; set; }
}

/// <summary>对齐模式改动系统设置前的状态，用于停止或崩溃后恢复。</summary>
public sealed class RelayRestoreState
{
    public string RelayDeviceId { get; set; } = "";
    public bool RelayWasMuted { get; set; }
    public Dictionary<string, string> OriginalDefaults { get; set; } = new();
}

/// <summary>保存在 %AppData%\AudioShare\settings.json，下次打开自动恢复。</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioShare", "settings.json");

    private static readonly object FileLock = new();

    /// <summary>直接模式下：声音来源设备，null 表示跟随系统默认。对齐模式下：中转设备，null 表示自动选择。</summary>
    public string? SourceDeviceId { get; set; }

    /// <summary>对齐模式（推荐）：把系统声音转到闲置设备并静音，所有设备都播放复制的声音。</summary>
    public bool AlignMode { get; set; } = true;

    public double BaseLatencyMs { get; set; } = 60;

    /// <summary>上次退出时是否正在共享，开机自启时用来决定是否自动开始。</summary>
    public bool WasRunning { get; set; }

    /// <summary>不为空表示系统设置被改过、还没恢复（例如程序崩溃了）。</summary>
    public RelayRestoreState? PendingRestore { get; set; }

    public Dictionary<string, DeviceSettings> Devices { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // 配置损坏时使用默认值
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // 保存失败不影响使用
        }
    }
}
