using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroMon.Services;

public sealed record UpdateInfo(string Version, string TagName, string ReleaseNotes, string DownloadUrl, string AssetName, long Size, string? Sha256);

public enum UpdateCheckStatus
{
    /// <summary>Check succeeded (an update may or may not be available).</summary>
    Ok,
    /// <summary>KUROMON_UPDATE_REPO is not set.</summary>
    NotConfigured,
    /// <summary>401: the token is expired, revoked or malformed.</summary>
    TokenInvalid,
    /// <summary>403/404 with a token: the token cannot read the repository.</summary>
    TokenNoAccess,
    /// <summary>404 without a token: repository missing or private.</summary>
    TokenMissing,
    /// <summary>Network error, rate limit or unexpected response.</summary>
    Error,
}

/// <param name="TokenExpires">Expiry reported by GitHub for fine-grained / expiring tokens.</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, DateTimeOffset? TokenExpires, string? Message)
{
    public bool IsAuthProblem => Status is UpdateCheckStatus.TokenInvalid or UpdateCheckStatus.TokenNoAccess or UpdateCheckStatus.TokenMissing;
}

/// <summary>
/// Checks GitHub Releases for a newer semantic version, downloads the installer (KuroMon-Setup-x.y.z.exe),
/// verifies its SHA-256 and runs it with /SILENT. The installer restarts KuroMon when it finishes.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _hasToken;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KuroMon", AppConfig.CurrentVersionText.TrimStart('v')));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        // The token is read when the service is created; a new service is created for every check,
        // so a renewed token in .env is picked up automatically.
        var token = AppConfig.GitHubToken;
        _hasToken = token is not null;
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Checks the latest release. <see cref="UpdateCheckResult.Update"/> is set when a newer version exists.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var repo = AppConfig.UpdateRepository;
        if (!repo.Contains('/'))
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, null, null);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, null, null, ex.Message);
        }

        using (response)
        {
            var expires = ReadTokenExpiry(response);
            var hasToken = _hasToken;

            switch ((int)response.StatusCode)
            {
                case 401:
                    return new UpdateCheckResult(UpdateCheckStatus.TokenInvalid, null, expires, "401 Unauthorized");
                case 403 when IsRateLimited(response):
                    return new UpdateCheckResult(UpdateCheckStatus.Error, null, expires, "GitHub API rate limit");
                case 403 or 404 when hasToken:
                    return new UpdateCheckResult(UpdateCheckStatus.TokenNoAccess, null, expires, $"{(int)response.StatusCode}");
                case 404:
                    return new UpdateCheckResult(UpdateCheckStatus.TokenMissing, null, expires, "404 Not Found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Error, null, expires, $"HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: ct).ConfigureAwait(false);

            UpdateInfo? update = null;
            if (release is { Draft: false, Prerelease: false }
                && SemVer.TagToVersion(release.TagName) is { } latest
                && SemVer.IsNewer(latest, AppConfig.CurrentVersionText.TrimStart('v')))
            {
                var asset = release.Assets.FirstOrDefault(a => a.Name.StartsWith("KuroMon-Setup-", StringComparison.OrdinalIgnoreCase)
                                                               && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                if (asset is not null)
                {
                    // Private repositories need the API asset URL with an octet-stream Accept header.
                    var url = hasToken ? asset.Url : asset.BrowserDownloadUrl;
                    var sha = asset.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
                    update = new UpdateInfo(latest, release.TagName, release.Body ?? string.Empty, url, asset.Name, asset.Size, sha);
                }
            }
            return new UpdateCheckResult(UpdateCheckStatus.Ok, update, expires, null);
        }
    }

    /// <summary>
    /// GitHub adds "GitHub-Authentication-Token-Expiration" (e.g. "2026-12-25 00:00:00 UTC") for expiring tokens.
    /// </summary>
    private static DateTimeOffset? ReadTokenExpiry(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("GitHub-Authentication-Token-Expiration", out var values)) return null;
        var raw = values.FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        raw = raw.Replace(" UTC", " +00:00", StringComparison.OrdinalIgnoreCase);
        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var v) && v.FirstOrDefault() == "0";

    /// <summary>Downloads the installer into %TEMP%, checks its size and SHA-256, and returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "KuroMon_update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, info.AssetName);

        using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? info.Size;
        await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var dst = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done * 100d / total);
            }
        }

        var length = new FileInfo(target).Length;
        if (length == 0 || (info.Size > 0 && length != info.Size)) throw new IOException("ダウンロードしたファイルのサイズが一致しません。");
        if (info.Sha256 is { } expected)
        {
            await using var fs = File.OpenRead(target);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("ダウンロードしたファイルが壊れているか、改ざんされています。");
            }
        }
        return target;
    }

    /// <summary>Starts the installer silently. The caller exits KuroMon right after; the installer restarts it.</summary>
    public static void RunInstaller(string installer)
    {
        Process.Start(new ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(installer)!,
        });
    }

    public void Dispose() => _http.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
