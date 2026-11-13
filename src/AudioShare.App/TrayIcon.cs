using System.Windows;
using AudioShare.App.ViewModels;
using Forms = System.Windows.Forms;

namespace AudioShare.App;

/// <summary>任务栏通知区域图标：关掉窗口后继续共享，右键菜单可以开始/停止和退出。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _autoStartItem;
    private readonly MainViewModel _viewModel;

    public TrayIcon(MainViewModel viewModel, Action showWindow, Action exit)
    {
        _viewModel = viewModel;

        _toggleItem = new Forms.ToolStripMenuItem("", null, (_, _) => _viewModel.Toggle());
        _autoStartItem = new Forms.ToolStripMenuItem("开机自动启动", null, (_, _) => _viewModel.AutoStartEnabled = !_viewModel.AutoStartEnabled);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("打开主界面", null, (_, _) => showWindow()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出", null, (_, _) => exit()));
        menu.Opening += (_, _) => _autoStartItem.Checked = _viewModel.AutoStartEnabled;

        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) showWindow();
        };

        _viewModel.StateChanged += Update;
        Update();
    }

    public void ShowHint(string message) =>
        _icon.ShowBalloonTip(3000, "AudioShare", message, Forms.ToolTipIcon.None);

    private void Update()
    {
        _toggleItem.Text = _viewModel.IsRunning ? "停止共享" : "开始共享";
        // 提示文字最多 63 个字符
        string tip = $"AudioShare · {(_viewModel.IsRunning ? "共享中" : "未共享")}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    public void Dispose()
    {
        _viewModel.StateChanged -= Update;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
