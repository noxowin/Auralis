using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace AudioShare.App.Audio;

/// <summary>
/// 切换系统默认播放设备。Windows 没有公开这个 API，这里用的是系统声音设置面板自己用的
/// IPolicyConfig 接口（SoundSwitch、EarTrumpet 等工具也用它），Windows 7 到 11 都可用。
/// </summary>
public static class DefaultDeviceSwitcher
{
    public static readonly Role[] AllRoles = { Role.Console, Role.Multimedia, Role.Communications };

    public static void SetDefault(string deviceId, Role role)
    {
        var config = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
        }
        finally
        {
            Marshal.ReleaseComObject(config);
        }
    }

    public static void SetDefaultForAllRoles(string deviceId)
    {
        foreach (var role in AllRoles) SetDefault(deviceId, role);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    // 只调用 SetDefaultEndpoint；前面的方法只是为了占住虚函数表的位置，顺序不能改
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
