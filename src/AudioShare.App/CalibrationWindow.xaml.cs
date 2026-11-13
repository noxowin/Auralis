using System.ComponentModel;
using System.Windows;
using AudioShare.App.ViewModels;

namespace AudioShare.App;

public partial class CalibrationWindow : Window
{
    private readonly MainViewModel _viewModel;
    private CancellationTokenSource? _cancellation;

    public CalibrationWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;

        var mics = viewModel.GetMicrophones();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedItem = mics.FirstOrDefault(m => m.Id == viewModel.DefaultMicrophoneId(mics));

        if (mics.Count == 0)
            ShowResult("没有找到可用的麦克风，无法自动校准。可以手动拖动延迟滑块对齐。", false);
        else if (viewModel.CalibrationBlocker() is { } blocker)
            ShowResult(blocker, false);
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_cancellation != null)
        {
            _cancellation.Cancel();
            return;
        }
        if (MicCombo.SelectedItem is not SourceOption mic || mic.Id == null) return;

        _cancellation = new CancellationTokenSource();
        StartText.Text = "取消";
        MicCombo.IsEnabled = false;
        CloseButton.IsEnabled = false;
        var progress = new Progress<string>(text => ShowResult(text, null));

        var (ok, message) = await _viewModel.CalibrateAsync(mic.Id, progress, _cancellation.Token);

        _cancellation.Dispose();
        _cancellation = null;
        StartText.Text = ok ? "重新校准" : "再试一次";
        MicCombo.IsEnabled = true;
        CloseButton.IsEnabled = true;
        ShowResult(message, ok);
    }

    /// <param name="ok">true 成功（绿色），false 失败（红色），null 进行中（灰色）</param>
    private void ShowResult(string text, bool? ok)
    {
        ResultText.Visibility = Visibility.Visible;
        ResultText.Text = text;
        ResultText.Foreground = (System.Windows.Media.Brush)FindResource(
            ok == true ? "SuccessBrush" : ok == false ? "ErrorBrush" : "SubTextBrush");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // 校准进行中先取消，等它把设备恢复正常再关
        if (_cancellation != null)
        {
            _cancellation.Cancel();
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
