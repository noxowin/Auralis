using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AudioShare.App.Audio;
using NAudio.CoreAudioApi;

namespace AudioShare.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly DeviceWatcher _watcher;
    private readonly AudioRouter _router = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly RelayManager _relay;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _meterTimer;
    private bool _refreshPending;

    private SourceOption? _selectedSource;
    private bool _isRunning;
    private string _statusText = "";
    private double _baseLatencyMs;
    private bool _alignMode;
    private bool _disposed;

    public MainViewModel()
    {
        _relay = new RelayManager(_enumerator, _settings);
        // 上次没有正常恢复（崩溃、被强制结束、直接关机），先把系统设置改回去
        if (_relay.IsActive) _relay.Restore();

        _baseLatencyMs = _settings.BaseLatencyMs;
        _alignMode = _settings.AlignMode;
        ToggleCommand = new RelayCommand(Toggle);
        RefreshCommand = new RelayCommand(RefreshDevices);

        _watcher = new DeviceWatcher(_enumerator);
        _watcher.DevicesChanged += ScheduleRefresh;
        _watcher.DefaultRenderDeviceChanged += () => _ui.BeginInvoke(OnDefaultDeviceChanged);
        _router.CaptureStopped += ex => _ui.BeginInvoke(() => OnCaptureLost(ex));
        _router.OutputStopped += (id, ex) => _ui.BeginInvoke(() => OnOutputLost(id, ex));

        RefreshDevices();
        _selectedSource = Sources.FirstOrDefault(s => s.Id == _settings.SourceDeviceId) ?? Sources.FirstOrDefault();
        UpdateSourceFlags();
        UpdateStatusText();

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateMeters(), _ui);
        _meterTimer.Start();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public ObservableCollection<SourceOption> Sources { get; } = new();

    public ICommand ToggleCommand { get; }

    public ICommand RefreshCommand { get; }

    /// <summary>运行状态或状态文字变化，托盘图标用来刷新菜单和提示。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 对齐模式（推荐）：系统声音转到一个闲置设备并静音，所有设备都播放复制的声音，延迟可以完全对齐。
    /// 直接模式：默认设备照常播放，只给其他设备送一份。默认设备总会比其他设备早一点。
    /// </summary>
    public bool AlignMode
    {
        get => _alignMode;
        set
        {
            if (!Set(ref _alignMode, value)) return;
            _settings.AlignMode = value;
            _settings.SourceDeviceId = null;
            OnPropertyChanged(nameof(SourceTitle));
            OnPropertyChanged(nameof(SourceHint));
            bool wasRunning = IsRunning;
            if (wasRunning) Stop(keepRunningFlag: true);
            RebuildSources();
            if (wasRunning) Start();
            else UpdateSourceFlags();
            _settings.Save();
        }
    }

    public bool AutoStartEnabled
    {
        get
        {
            try { return AutoStart.IsEnabled; }
            catch { return false; }
        }
        set
        {
            try { AutoStart.IsEnabled = value; }
            catch (Exception ex) { StatusText = $"无法设置开机自启：{ex.Message}"; }
            OnPropertyChanged();
        }
    }

    public string SourceTitle => AlignMode ? "中转设备" : "声音来源";

    public string SourceHint => AlignMode
        ? "开始后，系统声音会暂时转到这个设备并把它静音，所有打开的设备都播放同一份声音，延迟可以完全对齐。停止或退出时自动恢复原来的设置。"
        : "这个设备照常直接播放，其他打开的设备播放复制的声音。它总会比其他设备早一点，如果要对齐，请打开上面的对齐模式。";

    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (value == null || !Set(ref _selectedSource, value)) return;
            _settings.SourceDeviceId = value.Id;
            if (IsRunning) Restart();
            else UpdateSourceFlags();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(ToggleText));
            StateChanged?.Invoke();
        }
    }

    public string ToggleText => IsRunning ? "停止共享" : "开始共享";

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value)) StateChanged?.Invoke();
        }
    }

    /// <summary>所有设备共同的基础缓冲延迟。网络/蓝牙不稳时调大，追求跟手时调小。</summary>
    public double BaseLatencyMs
    {
        get => _baseLatencyMs;
        set
        {
            if (!Set(ref _baseLatencyMs, Math.Round(value / 5) * 5)) return;
            _settings.BaseLatencyMs = _baseLatencyMs;
            foreach (var vm in Devices)
                if (_router.Find(vm.Id) is { } ch) ch.TargetLatencyMs = TargetLatency(vm);
        }
    }

    private int TargetLatency(DeviceViewModel vm) => (int)(BaseLatencyMs + vm.DelayMs);

    public void Toggle()
    {
        if (IsRunning) Stop();
        else Start();
    }

    /// <summary>开机自启时调用：上次退出时在共享，就自动接着共享。</summary>
    public void ResumeIfWasRunning()
    {
        if (_settings.WasRunning && !IsRunning) Start();
    }

    private void Start()
    {
        MMDevice? source;
        if (AlignMode)
        {
            var enabled = Devices.Where(d => d.IsEnabled).Select(d => d.Id).ToHashSet();
            source = _relay.PickRelay(SelectedSource?.Id, enabled);
            if (source == null)
            {
                StatusText = "没有闲置的设备可以当中转。请关掉一个设备的开关，或者关闭对齐模式";
                return;
            }

            try
            {
                _relay.Activate(source);
            }
            catch (Exception ex)
            {
                _relay.Restore();
                StatusText = $"无法切换默认设备：{ex.Message}";
                return;
            }
        }
        else
        {
            source = ResolveSource();
            if (source == null)
            {
                StatusText = "找不到声音来源设备，请检查是否有可用的播放设备";
                return;
            }
        }

        try
        {
            _router.Start(source);
        }
        catch (Exception ex)
        {
            _relay.Restore();
            StatusText = $"无法捕获系统声音：{ex.Message}";
            return;
        }

        IsRunning = true;
        _settings.WasRunning = true;
        UpdateSourceFlags();
        SyncOutputs();
        UpdateStatusText();
        _settings.Save();
    }

    /// <param name="keepRunningFlag">退出程序时为 true：记住"正在共享"，下次开机自启时自动继续。</param>
    private void Stop(bool keepRunningFlag = false)
    {
        _router.Stop();
        _relay.Restore();
        IsRunning = false;
        if (!keepRunningFlag) _settings.WasRunning = false;
        foreach (var vm in Devices) vm.HasError = false;
        UpdateSourceFlags();
        UpdateStatusText();
        _settings.Save();
    }

    private void Restart()
    {
        Stop(keepRunningFlag: true);
        Start();
    }

    private MMDevice? ResolveSource()
    {
        try
        {
            return SelectedSource?.Id is { } id
                ? _enumerator.GetDevice(id)
                : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return null;
        }
    }

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _ui.BeginInvoke(async () =>
        {
            // 插拔时系统会连续发多个通知，稍等一下合并处理
            await Task.Delay(400);
            _refreshPending = false;
            RefreshDevices();
        });
    }

    private void RefreshDevices()
    {
        var active = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        var ids = active.Select(d => d.ID).ToHashSet();

        foreach (var gone in Devices.Where(d => !ids.Contains(d.Id)).ToList())
        {
            _router.RemoveOutput(gone.Id);
            Devices.Remove(gone);
        }

        foreach (var device in active)
        {
            if (Devices.Any(d => d.Id == device.ID)) continue;

            var vm = new DeviceViewModel(device.ID, device.FriendlyName, DescribeFormat(device), OnDeviceChanged);
            if (_settings.Devices.TryGetValue(device.ID, out var saved))
            {
                // 先复制出来：给 vm 赋值会触发回调并改写 saved
                var (volume, delay, enabled) = (saved.Volume, saved.DelayMs, saved.Enabled);
                vm.Volume = volume;
                vm.DelayMs = delay;
                vm.IsEnabled = enabled;
            }
            Devices.Add(vm);
        }

        RebuildSources(active, SelectedSource?.Id);
        UpdateSourceFlags();
        if (IsRunning) SyncOutputs();
        UpdateStatusText();
    }

    private void RebuildSources() =>
        RebuildSources(_enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList(), _settings.SourceDeviceId);

    private void RebuildSources(List<MMDevice> active, string? selectedId)
    {
        // 下拉框第一项：对齐模式是"自动选择"，直接模式是"跟随系统默认"
        var options = new List<SourceOption> { new(null, AlignMode ? "自动选择闲置的设备" : "跟随系统默认播放设备") };
        options.AddRange(active.Select(d => new SourceOption(d.ID, d.FriendlyName)));
        Sources.Clear();
        foreach (var o in options) Sources.Add(o);
        _selectedSource = Sources.FirstOrDefault(s => s.Id == selectedId) ?? Sources[0];
        OnPropertyChanged(nameof(SelectedSource));
    }

    private static string DescribeFormat(MMDevice device)
    {
        try
        {
            var f = device.AudioClient.MixFormat;
            return $"{f.SampleRate / 1000.0:0.#} kHz · {f.Channels} 声道";
        }
        catch
        {
            return "";
        }
    }

    private void UpdateSourceFlags()
    {
        // 对齐模式还没开始时不标记：自动选择的中转设备要等开始时才确定，标记了会挡住它的开关
        string? sourceId = IsRunning ? _router.SourceDeviceId
            : AlignMode ? SelectedSource?.Id
            : ResolveSource()?.ID;
        foreach (var vm in Devices) vm.IsSource = vm.Id == sourceId;
    }

    /// <summary>让正在运行的输出和界面上的开关保持一致。</summary>
    private void SyncOutputs()
    {
        foreach (var vm in Devices)
        {
            bool should = IsRunning && vm.IsEnabled && !vm.IsSource;
            bool has = _router.Find(vm.Id) != null;

            if (should && !has)
            {
                try
                {
                    _router.AddOutput(_enumerator.GetDevice(vm.Id), TargetLatency(vm), (float)(vm.Volume / 100));
                    vm.HasError = false;
                }
                catch (Exception ex)
                {
                    vm.HasError = true;
                    vm.Status = $"无法打开：{ex.Message}";
                }
            }
            else if (!should && has)
            {
                _router.RemoveOutput(vm.Id);
            }
        }
        UpdateStatusText();
    }

    private void OnDeviceChanged(DeviceViewModel vm, string property)
    {
        if (!_settings.Devices.TryGetValue(vm.Id, out var saved))
            _settings.Devices[vm.Id] = saved = new DeviceSettings();
        saved.Enabled = vm.IsEnabled;
        saved.Volume = vm.Volume;
        saved.DelayMs = vm.DelayMs;

        var channel = _router.Find(vm.Id);
        switch (property)
        {
            case nameof(DeviceViewModel.IsEnabled):
                if (IsRunning) SyncOutputs();
                _settings.Save();
                break;
            case nameof(DeviceViewModel.Volume) when channel != null:
                channel.Volume = (float)(vm.Volume / 100);
                break;
            case nameof(DeviceViewModel.DelayMs) when channel != null:
                channel.TargetLatencyMs = TargetLatency(vm);
                break;
        }
    }

    private void OnDefaultDeviceChanged()
    {
        // 对齐模式下默认设备是我们自己改的，忽略
        if (AlignMode || SelectedSource?.Id != null) return;
        if (IsRunning) Restart();
        else UpdateSourceFlags();
    }

    private void OnCaptureLost(Exception? ex)
    {
        if (!IsRunning) return;
        Stop();
        StatusText = "声音来源设备已断开，已停止共享";
    }

    private void OnOutputLost(string id, Exception? ex)
    {
        _router.RemoveOutput(id);
        if (Devices.FirstOrDefault(d => d.Id == id) is { } vm)
        {
            vm.HasError = true;
            vm.Status = "设备已断开，重新连接后会自动恢复";
        }
        UpdateStatusText();
    }

    private void UpdateStatusText()
    {
        if (!IsRunning)
        {
            StatusText = "打开要同时播放的设备，然后点击「开始共享」";
            return;
        }
        int count = _router.Outputs.Count;
        StatusText = count == 0
            ? "正在捕获声音，但还没有打开任何其他设备"
            : AlignMode
                ? $"正在把电脑声音同步到 {count} 个设备"
                : $"正在把电脑声音同步到另外 {count} 个设备";
    }

    private void UpdateMeters()
    {
        foreach (var vm in Devices)
        {
            if (vm.IsSource)
            {
                vm.Level = IsRunning ? _router.SourcePeak : 0;
                vm.Status = !IsRunning ? "已选为中转设备"
                    : AlignMode ? "中转设备 · 已静音，不出声"
                    : "声音来源 · 本身就在播放";
                continue;
            }

            if (_router.Find(vm.Id) is { } ch)
            {
                vm.Level = ch.Peak;
                vm.Status = ch.IsWaiting ? "等待声音…" : $"同步中 · 缓冲 {ch.BufferedMs:0} ms";
            }
            else
            {
                vm.Level = 0;
                if (!vm.HasError) vm.Status = vm.IsEnabled ? (IsRunning ? "正在连接…" : "已选中") : "未启用";
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meterTimer.Stop();
        if (IsRunning) Stop(keepRunningFlag: true);
        _relay.Restore();
        _settings.Save();
        _router.Dispose();
        _watcher.Dispose();
        _enumerator.Dispose();
    }
}
