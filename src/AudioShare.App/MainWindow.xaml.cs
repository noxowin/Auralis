using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioShare.App.ViewModels;

namespace AudioShare.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayIcon _tray;
    private bool _exiting;
    private bool _hintShown;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _tray = new TrayIcon(_viewModel, ShowFromTray, ExitApp);
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    /// <summary>开机自启：不弹窗口，只在托盘里，上次在共享就接着共享。</summary>
    public void StartInTray()
    {
        _viewModel.ResumeIfWasRunning();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _viewModel.Dispose();
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }

    public void EmergencyRestore()
    {
        try { _viewModel.Dispose(); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            // 关窗口只是藏到托盘，声音继续
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _hintShown = true;
                _tray.ShowHint(_viewModel.IsRunning
                    ? "AudioShare 还在后台共享声音。右键托盘图标可以停止或退出。"
                    : "AudioShare 已缩小到托盘。右键托盘图标可以退出。");
            }
        }
        base.OnClosing(e);
    }

    private void OnCalibrate(object sender, RoutedEventArgs e)
    {
        new CalibrationWindow(_viewModel) { Owner = this }.ShowDialog();
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "保存方案", "给当前的设备设置起个名字，例如「蓝牙 + 有线看电影」",
            _viewModel.SelectedPreset ?? _viewModel.SuggestPresetName());
        if (!string.IsNullOrEmpty(name)) _viewModel.SavePreset(name);
    }

    private void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPreset is not { } name) return;
        if (MessageBox.Show(this, $"删除方案「{name}」？", "删除方案", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            _viewModel.DeleteSelectedPreset();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void UseDarkTitleBar()
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        int on = 1;
        var hwnd = new WindowInteropHelper(this).Handle;
        // Windows 10 1809 之前的系统不支持，忽略返回值即可
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }
}
