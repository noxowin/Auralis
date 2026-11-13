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
    private bool _mono;
    private double _bassDb, _midDb, _trebleDb;
    private bool _showEffects;

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

    /// <summary>左右声道合并，只戴一只耳机时也能听到完整内容。</summary>
    public bool Mono
    {
        get => _mono;
        set
        {
            if (Set(ref _mono, value)) _onChanged(this, nameof(Mono));
        }
    }

    public double BassDb
    {
        get => _bassDb;
        set { if (Set(ref _bassDb, Math.Round(value))) EffectsChanged(); }
    }

    public double MidDb
    {
        get => _midDb;
        set { if (Set(ref _midDb, Math.Round(value))) EffectsChanged(); }
    }

    public double TrebleDb
    {
        get => _trebleDb;
        set { if (Set(ref _trebleDb, Math.Round(value))) EffectsChanged(); }
    }

    /// <summary>卡片上的小字：音效有调整时提示一下。</summary>
    public string EffectsSummary => BassDb == 0 && MidDb == 0 && TrebleDb == 0 ? "音效" : "音效 · 已调整";

    /// <summary>界面上是否展开音效面板（不保存）。</summary>
    public bool ShowEffects
    {
        get => _showEffects;
        set => Set(ref _showEffects, value);
    }

    public void ResetEffects()
    {
        BassDb = 0;
        MidDb = 0;
        TrebleDb = 0;
    }

    private void EffectsChanged()
    {
        OnPropertyChanged(nameof(EffectsSummary));
        _onChanged(this, "Effects");
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
