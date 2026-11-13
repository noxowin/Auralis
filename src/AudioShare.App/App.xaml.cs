using System.Threading;
using System.Windows;

namespace AudioShare.App;

public partial class App : Application
{
    // 只允许运行一个实例：两个实例同时改默认设备会互相打架
    private const string InstanceName = "AudioShare.SingleInstance.7f3c";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            // 已经在运行（可能藏在托盘里）：通知它显示窗口，然后自己退出
            try { EventWaitHandle.OpenExisting(InstanceName + ".Show").Set(); } catch { }
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        var listener = new Thread(() =>
        {
            try
            {
                while (_showSignal.WaitOne())
                    Dispatcher.BeginInvoke(() => _window?.ShowFromTray());
            }
            catch (ObjectDisposedException)
            {
                // 程序退出时句柄被释放
            }
        }) { IsBackground = true };
        listener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "AudioShare 出错了", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        // 真正崩溃时尽量把默认设备改回去；万一没来得及，下次启动也会恢复
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _window?.EmergencyRestore();

        base.OnStartup(e);

        bool autoStarted = e.Args.Contains(AutoStart.Argument);
        _window = new MainWindow();
        if (autoStarted) _window.StartInTray();
        else _window.Show();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 注销或关机：恢复系统设置，并记住是否在共享
        _window?.ExitApp();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        if (_window != null) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
