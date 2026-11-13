using NAudio.CoreAudioApi;

namespace AudioShare.App.Audio;

/// <summary>
/// 对齐模式：把系统默认设备临时切到一个闲置的"中转设备"并静音。
/// 这样所有要出声的设备播放的都是复制出来的声音，可以用延迟滑块精确对齐；
/// 否则原来的默认设备总是直接出声，会比其他设备早，无法对齐。
///
/// 切换前的状态会立刻写进配置文件。即使程序崩溃或被强制结束，
/// 下次启动时也能把默认设备和静音状态改回去。
/// </summary>
public sealed class RelayManager
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly AppSettings _settings;

    public RelayManager(MMDeviceEnumerator enumerator, AppSettings settings)
    {
        _enumerator = enumerator;
        _settings = settings;
    }

    public bool IsActive => _settings.PendingRestore != null;

    /// <summary>
    /// 自动挑一个中转设备：不在要出声的列表里的设备，优先显示器/HDMI 和电脑自带扬声器，
    /// 最后才考虑耳机（耳机可能有人戴着）。
    /// </summary>
    public MMDevice? PickRelay(string? preferredId, IReadOnlySet<string> enabledOutputs)
    {
        var candidates = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(d => !enabledOutputs.Contains(d.ID))
            .ToList();

        if (preferredId != null && candidates.FirstOrDefault(d => d.ID == preferredId) is { } preferred)
            return preferred;

        return candidates
            .OrderBy(d => RelayRank(d.FriendlyName))
            .FirstOrDefault();
    }

    private static int RelayRank(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("hdmi") || n.Contains("display") || n.Contains("monitor") || n.Contains("显示器") || n.Contains("nvidia") || n.Contains("amd")) return 0;
        if (n.Contains("speaker") || n.Contains("扬声器") || n.Contains("realtek")) return 1;
        if (n.Contains("bluetooth") || n.Contains("蓝牙") || n.Contains("headphone") || n.Contains("耳机") || n.Contains("headset")) return 3;
        return 2;
    }

    public void Activate(MMDevice relay)
    {
        if (IsActive) Restore();

        var state = new RelayRestoreState
        {
            RelayDeviceId = relay.ID,
            RelayWasMuted = relay.AudioEndpointVolume.Mute,
        };
        foreach (var role in DefaultDeviceSwitcher.AllRoles)
        {
            try { state.OriginalDefaults[role.ToString()] = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role).ID; }
            catch { /* 这个角色没有默认设备 */ }
        }

        // 先落盘再改系统设置，保证随时可恢复
        _settings.PendingRestore = state;
        _settings.Save();

        DefaultDeviceSwitcher.SetDefaultForAllRoles(relay.ID);
        relay.AudioEndpointVolume.Mute = true;
    }

    /// <summary>把默认设备和中转设备的静音状态改回去。设备已经拔掉时跳过那一项。</summary>
    public void Restore()
    {
        var state = _settings.PendingRestore;
        if (state == null) return;

        try
        {
            var relay = _enumerator.GetDevice(state.RelayDeviceId);
            if (relay.State == DeviceState.Active) relay.AudioEndpointVolume.Mute = state.RelayWasMuted;
        }
        catch { }

        foreach (var role in DefaultDeviceSwitcher.AllRoles)
        {
            if (!state.OriginalDefaults.TryGetValue(role.ToString(), out var id)) continue;
            try
            {
                if (_enumerator.GetDevice(id).State == DeviceState.Active)
                    DefaultDeviceSwitcher.SetDefault(id, role);
            }
            catch { }
        }

        _settings.PendingRestore = null;
        _settings.Save();
    }
}
