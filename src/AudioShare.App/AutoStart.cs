using Microsoft.Win32;

namespace AudioShare.App;

/// <summary>通过当前用户的 Run 注册表项实现开机自启，不需要管理员权限。</summary>
public static class AutoStart
{
    public const string Argument = "--autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioShare";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value == Command;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, false);
        }
    }
}
