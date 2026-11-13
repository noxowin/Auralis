using System.Windows.Threading;

namespace AudioShare.App.ViewModels;

/// <summary>检查更新：启动后稍等一会儿查一次，之后每天查一次。发现新版本时在窗口顶部显示提示条。</summary>
public sealed class UpdateViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private UpdateInfo? _available;
    private bool _isBusy;
    private string _message = "";

    public UpdateViewModel(AppSettings settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = TimeSpan.FromHours(24);
            if (_settings.CheckUpdates) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    public string CurrentVersionText => $"当前版本 {Updater.CurrentVersion.ToString(3)}";

    /// <summary>发现新版本时触发，托盘图标会弹出提示。</summary>
    public event Action<UpdateInfo>? Found;

    public bool CheckUpdates
    {
        get => _settings.CheckUpdates;
        set
        {
            _settings.CheckUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public UpdateInfo? Available
    {
        get => _available;
        private set
        {
            if (!Set(ref _available, value)) return;
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(BannerText));
        }
    }

    public bool HasUpdate => Available != null;

    public string BannerText => Available == null ? "" : $"发现新版本 {Available.Version.ToString(3)}";

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>检查或下载过程中的提示文字。</summary>
    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public async Task CheckAsync(bool manual)
    {
        if (IsBusy) return;
        IsBusy = true;
        if (manual) Message = "正在检查…";
        try
        {
            var update = await Updater.CheckAsync(CancellationToken.None);
            // 用户跳过的版本，自动检查时不再提醒；手动检查照常显示
            if (update != null && !manual && update.Tag == _settings.SkippedVersion) update = null;
            Available = update;
            Message = update != null ? "" : manual ? "已经是最新版本" : "";
            if (update != null) Found?.Invoke(update);
        }
        catch (Exception ex)
        {
            if (manual) Message = $"检查失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Skip()
    {
        if (Available == null) return;
        _settings.SkippedVersion = Available.Tag;
        _settings.Save();
        Available = null;
    }

    /// <summary>下载并替换。成功时返回新 exe 路径（调用方退出并重启），失败返回 null。</summary>
    public async Task<string?> InstallAsync()
    {
        if (Available is not { } update || IsBusy) return null;
        IsBusy = true;
        try
        {
            var progress = new Progress<double>(p => Message = $"正在下载… {p:P0}");
            return await Updater.DownloadAndReplaceAsync(update, progress, CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
            Message = "程序所在的文件夹没有写入权限，已打开下载页面，请手动下载替换。";
            Updater.OpenReleasesPage(update.PageUrl);
            return null;
        }
        catch (Exception ex)
        {
            Message = $"更新失败：{ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Stop() => _timer.Stop();
}
