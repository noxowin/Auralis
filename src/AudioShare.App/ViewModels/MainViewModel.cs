using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using AudioShare.App.Audio;
using AudioShare.Core;
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
    private readonly Dictionary<string, int> _crackleCounts = new();
    private long _lastAutoRaiseMs;
    private bool _isCalibrating;
    private string? _selectedPreset;
    private readonly DispatcherTimer _recoveryTimer;
    private bool _resumePending;
    private long _resumeSinceMs;

    public MainViewModel()
    {
        _relay = new RelayManager(_enumerator, _settings);
        // 上次没有正常恢复（崩溃、被强制结束、直接关机），先把系统设置改回去
        if (_relay.IsActive) _relay.Restore();

        Update = new UpdateViewModel(_settings);
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
        RebuildPresetNames();
        _selectedSource = Sources.FirstOrDefault(s => s.Id == _settings.SourceDeviceId) ?? Sources.FirstOrDefault();
        UpdateSourceFlags();
        UpdateStatusText();

        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) => UpdateMeters(), _ui);
        _meterTimer.Start();

        _recoveryTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => Recover(), _ui);
        _recoveryTimer.Start();
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

    public UpdateViewModel Update { get; }

    /// <summary>全局快捷键开关，窗口据此注册/注销。</summary>
    public bool HotkeysEnabled
    {
        get => _settings.HotkeysEnabled;
        set
        {
            _settings.HotkeysEnabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public void ShowStatus(string text) => StatusText = text;

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
        if (IsRunning || _resumePending)
        {
            _resumePending = false;
            Stop();
        }
        else
        {
            Start();
        }
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
                ApplyDeviceSettings(vm, saved.Clone());
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
                    var channel = _router.AddOutput(_enumerator.GetDevice(vm.Id), TargetLatency(vm), (float)(vm.Volume / 100));
                    channel.Mono = vm.Mono;
                    channel.SetEq(EqOf(vm));
                    _crackleCounts[vm.Id] = 0;
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
        saved.Mono = vm.Mono;
        saved.BassDb = vm.BassDb;
        saved.MidDb = vm.MidDb;
        saved.TrebleDb = vm.TrebleDb;

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
            case nameof(DeviceViewModel.Mono):
                if (channel != null) channel.Mono = vm.Mono;
                _settings.Save();
                break;
            case "Effects":
                channel?.SetEq(EqOf(vm));
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

    /// <summary>
    /// 来源设备（或中转设备）被拔掉：先停下并恢复系统设置，然后每隔几秒重新开始，
    /// 对齐模式会自动换一个中转设备。用户手动点停止才会放弃。
    /// </summary>
    private void OnCaptureLost(Exception? ex)
    {
        if (!IsRunning) return;
        Stop(keepRunningFlag: true);
        _resumePending = true;
        _resumeSinceMs = Environment.TickCount64;
        StatusText = "声音来源设备断开了，正在自动重新开始…";
    }

    /// <summary>每 3 秒检查一次：断开的设备重新连上、来源设备恢复后，自动接着播放。</summary>
    private void Recover()
    {
        if (_disposed || IsCalibrating) return;

        if (_resumePending && !IsRunning)
        {
            Start();
            if (IsRunning)
            {
                _resumePending = false;
                StateNotice?.Invoke("声音来源已恢复，继续共享");
            }
            else if (Environment.TickCount64 - _resumeSinceMs > 120_000)
            {
                _resumePending = false;
                _settings.WasRunning = false;
                StatusText = "声音来源设备一直没有恢复，已停止共享";
            }
            return;
        }

        // 开关打开、但没在播放的设备（蓝牙断开后重连、切换模式导致中断等）：重新打开
        if (IsRunning && Devices.Any(d => d.IsEnabled && !d.IsSource && _router.Find(d.Id) == null))
            SyncOutputs();
    }

    /// <summary>需要提醒用户的事件（窗口可能藏在托盘里），托盘图标会弹出提示。</summary>
    public event Action<string>? StateNotice;

    private void OnOutputLost(string id, Exception? ex)
    {
        _router.RemoveOutput(id);
        if (Devices.FirstOrDefault(d => d.Id == id) is { } vm)
        {
            vm.HasError = true;
            vm.Status = "连接中断，正在自动重连…";
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
                vm.Status = _isCalibrating ? "校准中…"
                    : ch.IsWaiting ? "等待声音…"
                    : $"同步中 · 缓冲 {ch.BufferedMs:0} ms";
                CheckCrackles(vm, ch);
            }
            else
            {
                vm.Level = 0;
                if (!vm.HasError) vm.Status = vm.IsEnabled ? (IsRunning ? "正在连接…" : "已选中") : "未启用";
            }
        }
    }

    /// <summary>
    /// 自动防杂音：某个设备因为缓冲不够断续了，就把所有设备共用的缓冲加 10 ms。
    /// 所有设备一起加，所以已经调好的对齐不会被打乱。
    /// </summary>
    private void CheckCrackles(DeviceViewModel vm, OutputChannel channel)
    {
        int count = channel.CrackleCount;
        int last = _crackleCounts.GetValueOrDefault(vm.Id);
        _crackleCounts[vm.Id] = count;
        if (count <= last || _isCalibrating) return;

        // 刚加过就先等缓冲蓄满再看，避免一次断续连加好几次
        long now = Environment.TickCount64;
        if (now - _lastAutoRaiseMs < 3000 || BaseLatencyMs >= MaxBaseLatencyMs) return;
        _lastAutoRaiseMs = now;
        BaseLatencyMs = Math.Min(MaxBaseLatencyMs, BaseLatencyMs + 10);
        StatusText = $"「{vm.Name}」出现断续，已自动把缓冲加到 {BaseLatencyMs:0} ms";
    }

    private const double MaxBaseLatencyMs = 300;

    // ───────────── 自动校准 ─────────────

    public bool IsCalibrating
    {
        get => _isCalibrating;
        private set => Set(ref _isCalibrating, value);
    }

    /// <summary>不能校准时返回原因；可以校准时返回 null。</summary>
    public string? CalibrationBlocker()
    {
        if (!IsRunning) return "请先点「开始共享」，再来校准。";
        if (!AlignMode) return "校准需要对齐模式：直接模式下来源设备不经过本程序，无法测量。请先打开对齐模式。";
        if (_router.Outputs.Count < 2) return "至少要打开两台设备才需要对齐。";
        return null;
    }

    public IReadOnlyList<SourceOption> GetMicrophones() =>
        _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new SourceOption(d.ID, d.FriendlyName))
            .ToList();

    /// <summary>
    /// 默认麦克风：上次用过的 → 系统默认（但避开蓝牙耳机的麦克风，打开它会让蓝牙耳机切到低音质的通话模式）→ 第一个。
    /// </summary>
    public string? DefaultMicrophoneId(IReadOnlyList<SourceOption> mics)
    {
        if (mics.Any(m => m.Id == _settings.CalibrationMicId)) return _settings.CalibrationMicId;
        static bool IsBluetooth(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("bluetooth") || n.Contains("蓝牙") || n.Contains("hands-free") || n.Contains("免提") || n.Contains("headset");
        }
        try
        {
            var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            if (!IsBluetooth(def.FriendlyName)) return def.ID;
        }
        catch { }
        return (mics.FirstOrDefault(m => !IsBluetooth(m.Name)) ?? mics.FirstOrDefault())?.Id;
    }

    /// <summary>运行校准，成功时自动应用新的延迟值。返回给用户看的结果说明。</summary>
    public async Task<(bool ok, string message)> CalibrateAsync(string micId, IProgress<string> progress, CancellationToken cancellation)
    {
        if (CalibrationBlocker() is { } blocker) return (false, blocker);

        var outputs = Devices.Where(d => _router.Find(d.Id) != null).ToList();
        _settings.CalibrationMicId = micId;
        IsCalibrating = true;
        try
        {
            var result = await Calibrator.RunAsync(
                _router, _enumerator.GetDevice(_router.SourceDeviceId!), _enumerator.GetDevice(micId),
                outputs.Select(d => d.Id).ToList(), progress, cancellation);

            if (!result.Success)
            {
                // 失败信息里的设备编号换成设备名
                string msg = result.Message;
                for (int i = 0; i < outputs.Count; i++) msg = msg.Replace($"第 {i + 1} 台设备", $"「{outputs[i].Name}」");
                return (false, msg);
            }

            var current = outputs.ToDictionary(d => d.Id, d => d.DelayMs);
            var suggested = CalibrationAnalyzer.SuggestDelays(result.Devices, current);
            if (suggested.Values.Any(v => v > 500))
                return (false, "测得的延迟差超过 500 ms，结果可能不可靠。请让环境安静一些、把耳机靠近麦克风再试一次。");

            foreach (var d in outputs) d.DelayMs = suggested[d.Id];
            _settings.Save();

            var slowest = outputs.First(d => suggested[d.Id] == 0);
            var lines = outputs.Where(d => d != slowest)
                .Select(d => $"「{d.Name}」比它快 {suggested[d.Id]:0} ms，已自动延后");
            return (true, $"校准完成。最慢的是「{slowest.Name}」。\n" + string.Join("\n", lines));
        }
        catch (OperationCanceledException)
        {
            return (false, "已取消校准。");
        }
        catch (Exception ex)
        {
            return (false, $"校准失败：{ex.Message}");
        }
        finally
        {
            IsCalibrating = false;
        }
    }

    // ───────────── 方案 ─────────────

    public ObservableCollection<string> PresetNames { get; } = new();

    /// <summary>选中一个方案就立即应用。</summary>
    public string? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!Set(ref _selectedPreset, value) || value == null) return;
            if (_settings.Presets.TryGetValue(value, out var preset)) ApplyPreset(preset);
            StatusText = $"已切换到方案「{value}」";
        }
    }

    public void SavePreset(string name)
    {
        _settings.Presets[name] = new Preset
        {
            AlignMode = AlignMode,
            BaseLatencyMs = BaseLatencyMs,
            Devices = Devices.ToDictionary(d => d.Id, d => new DeviceSettings
            {
                Enabled = d.IsEnabled, Volume = d.Volume, DelayMs = d.DelayMs, Mono = d.Mono,
                BassDb = d.BassDb, MidDb = d.MidDb, TrebleDb = d.TrebleDb,
            }),
        };
        _settings.Save();
        RebuildPresetNames();
        _selectedPreset = name;
        OnPropertyChanged(nameof(SelectedPreset));
        StatusText = $"已保存方案「{name}」";
    }

    public void DeleteSelectedPreset()
    {
        if (_selectedPreset == null) return;
        _settings.Presets.Remove(_selectedPreset);
        _settings.Save();
        StatusText = $"已删除方案「{_selectedPreset}」";
        _selectedPreset = null;
        RebuildPresetNames();
        OnPropertyChanged(nameof(SelectedPreset));
    }

    /// <summary>快捷键 Ctrl+Alt+数字：按下拉框里的顺序切换方案。</summary>
    public void SelectPresetByIndex(int index)
    {
        if (index < 0 || index >= PresetNames.Count)
        {
            StateNotice?.Invoke($"没有第 {index + 1} 个方案");
            return;
        }
        string name = PresetNames[index];
        if (SelectedPreset == name) ApplyPreset(_settings.Presets[name]);
        else SelectedPreset = name;
        if (!IsRunning) Start();
        StateNotice?.Invoke($"已切换到方案「{PresetNames[index]}」");
    }

    private static EqSettings EqOf(DeviceViewModel vm) => new(vm.BassDb, vm.MidDb, vm.TrebleDb);

    public string SuggestPresetName()
    {
        for (int i = 1; ; i++)
            if (!_settings.Presets.ContainsKey($"方案 {i}")) return $"方案 {i}";
    }

    private void RebuildPresetNames()
    {
        PresetNames.Clear();
        foreach (var n in _settings.Presets.Keys.OrderBy(n => n)) PresetNames.Add(n);
    }

    private void ApplyPreset(Preset preset)
    {
        BaseLatencyMs = preset.BaseLatencyMs;
        foreach (var vm in Devices)
            ApplyDeviceSettings(vm, preset.Devices.TryGetValue(vm.Id, out var s) ? s.Clone() : new DeviceSettings());
        // 对齐模式切换会自动重启共享
        AlignMode = preset.AlignMode;
        _settings.Save();
    }

    private static void ApplyDeviceSettings(DeviceViewModel vm, DeviceSettings s)
    {
        vm.Volume = s.Volume;
        vm.DelayMs = s.DelayMs;
        vm.Mono = s.Mono;
        vm.BassDb = s.BassDb;
        vm.MidDb = s.MidDb;
        vm.TrebleDb = s.TrebleDb;
        vm.IsEnabled = s.Enabled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meterTimer.Stop();
        _recoveryTimer.Stop();
        Update.Stop();
        if (IsRunning) Stop(keepRunningFlag: true);
        _relay.Restore();
        _settings.Save();
        _router.Dispose();
        _watcher.Dispose();
        _enumerator.Dispose();
    }
}
