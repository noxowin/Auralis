using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AudioShare.App;

public sealed record UpdateInfo(Version Version, string Tag, string Notes, string DownloadUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// 从 GitHub Releases 检查和下载新版本。程序保持单个 exe、不需要安装：
/// 运行中的 exe 不能覆盖，但可以改名，所以先把自己改名成 .old，再把新版放到原来的位置，然后重启。
/// </summary>
public static class Updater
{
    public const string Repository = "noxowin/AudioShare";
    public const string AssetName = "AudioShare.exe";
    public const string UpdatedArgument = "--updated";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>有比当前更新的版本时返回它，否则返回 null。网络出错时抛出异常。</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellation)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 还没有发布过版本
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation));
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= CurrentVersion) return null;

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;

            // GitHub 会给每个附件算 SHA-256（digest 字段，格式 "sha256:..."），有就校验
            string? sha = asset.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:")
                ? digest["sha256:".Length..]
                : null;
            return new UpdateInfo(
                version, tag,
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha,
                root.GetProperty("html_url").GetString() ?? ReleasesPage);
        }
        return null;
    }

    /// <summary>下载新版并替换当前 exe。返回新 exe 的路径，调用方负责退出并启动它。</summary>
    public static async Task<string> DownloadAndReplaceAsync(UpdateInfo update, IProgress<double> progress, CancellationToken cancellation)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("找不到程序路径");
        string newPath = exe + ".new", oldPath = exe + ".old";

        using (var response = await Http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            await using var target = File.Create(newPath);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellation)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellation);
                done += read;
                progress.Report(update.Size > 0 ? (double)done / update.Size : 0);
            }
        }

        try
        {
            if (new FileInfo(newPath).Length != update.Size)
                throw new InvalidDataException("下载的文件不完整");
            if (update.Sha256 != null)
            {
                await using var file = File.OpenRead(newPath);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation));
                if (!hash.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载的文件校验不通过");
            }

            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(exe, oldPath);
            File.Move(newPath, exe);
            return exe;
        }
        catch
        {
            TryDelete(newPath);
            throw;
        }
    }

    public static void Restart(string exe) =>
        Process.Start(new ProcessStartInfo(exe, UpdatedArgument) { UseShellExecute = false });

    /// <summary>启动时删掉上次更新留下的旧版本。</summary>
    public static void CleanupOldVersion()
    {
        if (Environment.ProcessPath is { } exe) TryDelete(exe + ".old");
    }

    public static void OpenReleasesPage(string? url = null) =>
        Process.Start(new ProcessStartInfo(url ?? ReleasesPage) { UseShellExecute = true });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AudioShare/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
