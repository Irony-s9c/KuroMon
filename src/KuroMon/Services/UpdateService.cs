using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroMon.Services;

public sealed record UpdateInfo(Version Version, string TagName, string ReleaseNotes, string DownloadUrl, string AssetName, long Size);

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
/// Checks GitHub Releases for a newer semantic version and swaps the running .exe.
/// Only the built .exe is published as a release asset; no source code is needed in the repository.
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
                && TryParseSemVer(release.TagName, out var latest)
                && latest > AppConfig.CurrentVersion)
            {
                var asset = release.Assets.FirstOrDefault(a => a.Name.Equals("KuroMon.exe", StringComparison.OrdinalIgnoreCase))
                            ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                                                  && !a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase));
                if (asset is not null)
                {
                    // Private repositories need the API asset URL with an octet-stream Accept header.
                    var url = hasToken ? asset.Url : asset.BrowserDownloadUrl;
                    update = new UpdateInfo(latest, release.TagName, release.Body ?? string.Empty, url, asset.Name, asset.Size);
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

    /// <summary>Downloads the new .exe into %TEMP% and returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "KuroMon_update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"KuroMon_{info.TagName}.exe");

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

        if (new FileInfo(target).Length == 0) throw new IOException("Downloaded file is empty.");
        return target;
    }

    /// <summary>
    /// Replaces the running .exe and starts the new one.
    /// Windows allows renaming a running executable, so the current file is moved into "backup\" first.
    /// </summary>
    public static void ApplyAndRestart(string downloadedExe)
    {
        var current = AppConfig.ExePath;
        var backupDir = Path.Combine(Path.GetDirectoryName(current)!, "backup");
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, $"KuroMon_{AppConfig.CurrentVersionText}_{DateTime.Now:yyyyMMdd_HHmmss}.exe");

        File.Move(current, backup);
        try
        {
            File.Copy(downloadedExe, current, overwrite: true);
        }
        catch
        {
            File.Move(backup, current); // roll back
            throw;
        }

        Process.Start(new ProcessStartInfo(current, App.AfterUpdateArgument)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(current)!,
        });
    }

    public static bool TryParseSemVer(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+']);
        if (cut >= 0) s = s[..cut];

        var parts = s.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch)) return false;

        version = new Version(major, minor, patch);
        return true;
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
    }
}
