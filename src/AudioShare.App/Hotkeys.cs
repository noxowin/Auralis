using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AudioShare.App;

/// <summary>
/// 全局快捷键：窗口藏在托盘里、正在玩游戏或看视频时也能用。
/// Ctrl+Alt+S 开始/停止共享，Ctrl+Alt+1~9 切换到第 N 个方案。
/// </summary>
public sealed class Hotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int ToggleId = 1;
    private const int PresetBaseId = 10;

    private readonly HwndSource _source;
    private readonly List<int> _registered = new();

    public Hotkeys(IntPtr hwnd)
    {
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
    }

    public event Action? TogglePressed;

    /// <summary>参数是方案序号，从 0 开始。</summary>
    public event Action<int>? PresetPressed;

    /// <summary>注册全部快捷键，返回被其他程序占用、没注册成功的按键说明。</summary>
    public IReadOnlyList<string> Register()
    {
        Unregister();
        var failed = new List<string>();
        if (!Add(ToggleId, 'S')) failed.Add("Ctrl+Alt+S");
        for (int i = 0; i < 9; i++)
            if (!Add(PresetBaseId + i, (uint)('1' + i))) failed.Add($"Ctrl+Alt+{i + 1}");
        return failed;
    }

    public void Unregister()
    {
        foreach (var id in _registered) UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
    }

    private bool Add(int id, uint key)
    {
        if (!RegisterHotKey(_source.Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key)) return false;
        _registered.Add(id);
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id == ToggleId) TogglePressed?.Invoke();
        else if (id >= PresetBaseId && id < PresetBaseId + 9) PresetPressed?.Invoke(id - PresetBaseId);
        handled = true;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
