using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace AudioShare.App.Audio;

/// <summary>监听设备插拔和默认设备变化。回调来自 COM 线程，订阅方需要切回 UI 线程。</summary>
public sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;

    public DeviceWatcher(MMDeviceEnumerator enumerator)
    {
        _enumerator = enumerator;
        _enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>设备列表发生变化（插入、拔出、启用、禁用）。</summary>
    public event Action? DevicesChanged;

    /// <summary>系统默认播放设备变化。</summary>
    public event Action? DefaultRenderDeviceChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();

    public void OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();

    public void OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultRenderDeviceChanged?.Invoke();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
    }
}
