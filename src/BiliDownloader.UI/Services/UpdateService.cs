using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace BiliDownloader.UI.Services;

/// <summary>一次更新检查的结果。</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>
/// 更新检查（设置项 CheckUpdateOnStartup）：查 GitHub 仓库的最新 Release。
/// 只做「告诉你有新版本 + 给个下载链接」，不自动下载、不自动替换，避免静默改动用户机器上的程序。
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/nie-crossroads/BiliDownloader/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>返回有新版本时的信息；已是最新或检查失败都返回 null（失败不该打扰用户）。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return null;

            // net48 的 ReadAsStreamAsync 没有 CancellationToken 重载
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var root = document.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag)) return null;

            var latest = Normalize(tag.GetString());
            if (latest is null || latest <= CurrentVersion) return null;

            var url = root.TryGetProperty("html_url", out var html) && html.GetString() is { Length: > 0 } page
                ? page
                : "https://github.com/nie-crossroads/BiliDownloader/releases";

            return new UpdateInfo(latest.ToString(), url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>把 "v1.2.0" / "1.2.0" / "1.2" 统一成可比较的 Version；解析不了返回 null。</summary>
    private static Version? Normalize(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var trimmed = tag.Trim().TrimStart('v', 'V');
        if (trimmed.Length == 0) return null;

        // 补齐成 x.y.z，Version 要求至少两段
        var parts = trimmed.Split('.');
        if (parts.Length == 1) trimmed += ".0.0";
        else if (parts.Length == 2) trimmed += ".0";

        return Version.TryParse(trimmed, out var version) ? version : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API 拒绝没有 User-Agent 的请求
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BiliDownloader", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}
