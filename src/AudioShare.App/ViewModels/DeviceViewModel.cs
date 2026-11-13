namespace AudioShare.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly Action<DeviceViewModel, string> _onChanged;
    private bool _isEnabled;
    private bool _isSource;
    private double _volume = 100;
    private double _delayMs;
    private string _status = "";
    private double _level;
    private bool _hasError;

    public DeviceViewModel(string id, string name, string format, Action<DeviceViewModel, string> onChanged)
    {
        Id = id;
        Name = name;
        Format = format;
        Icon = GuessIcon(name);
        _onChanged = onChanged;
    }

    public string Id { get; }

    public string Name { get; }

    public string Format { get; }

    public string Icon { get; }

    /// <summary>是否是声音来源设备。来源设备本身就在播放，不需要再送一份。</summary>
    public bool IsSource
    {
        get => _isSource;
        set
        {
            if (Set(ref _isSource, value)) OnPropertyChanged(nameof(CanToggle));
        }
    }

    public bool CanToggle => !IsSource;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (Set(ref _isEnabled, value)) _onChanged(this, nameof(IsEnabled));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, Math.Round(value))) _onChanged(this, nameof(Volume));
        }
    }

    public double DelayMs
    {
        get => _delayMs;
        set
        {
            if (Set(ref _delayMs, Math.Round(value / 5) * 5)) _onChanged(this, nameof(DelayMs));
        }
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool HasError
    {
        get => _hasError;
        set => Set(ref _hasError, value);
    }

    /// <summary>电平 0~1，带缓慢回落，看起来更顺滑。</summary>
    public double Level
    {
        get => _level;
        set => Set(ref _level, value > _level ? value : Math.Max(value, _level * 0.85));
    }

    // Segoe Fluent Icons / Segoe MDL2 Assets 字形
    private static string GuessIcon(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("bluetooth") || n.Contains("蓝牙") || n.Contains("airpods") || n.Contains("hands-free") || n.Contains("免提"))
            return "";
        if (n.Contains("headphone") || n.Contains("headset") || n.Contains("耳机") || n.Contains("buds"))
            return "";
        if (n.Contains("hdmi") || n.Contains("display") || n.Contains("monitor") || n.Contains("显示器") || n.Contains("nvidia") || n.Contains("amd"))
            return "";
        return "";
    }
}

public sealed record SourceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}
