using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroMon.Services;

/// <param name="DownloadUrl">Direct download of the installer (https), or empty when the server gives none.</param>
/// <param name="Sha256">SHA-256 of the installer; the update is installed only when the download matches it.</param>
/// <param name="PageUrl">Download page, opened in the browser when the update cannot be installed automatically.</param>
public sealed record UpdateInfo(string Version, string TagName, string ReleaseNotes, string DownloadUrl, string AssetName, string? Sha256, string PageUrl)
{
    public bool CanInstall => DownloadUrl.Length > 0 && Sha256 is not null;
}

public enum UpdateCheckStatus
{
    /// <summary>Check succeeded (an update may or may not be available).</summary>
    Ok,
    /// <summary>Network error or unexpected response.</summary>
    Error,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, string? Message);

/// <summary>
/// Checks the Musubi server for a newer version (GET /api/version/kuromon: latest_version, file_url, sha256,
/// download_url, release_notes), downloads the installer, verifies its SHA-256 and runs it with /SILENT.
/// The installer restarts KuroMon when it finishes. Nothing is sent but the GET itself.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string AppId = "kuromon";
    public const string DefaultServerUrl = "https://musubi-bot.xyz";
    private const long MaxInstallerBytes = 1024L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KuroMon", AppConfig.CurrentVersionText.TrimStart('v')));
    }

    /// <summary>
    /// The update server decides which installer gets run, so release builds always use the built-in one.
    /// Debug builds can point at a test server with KUROMON_MUSUBI_URL.
    /// </summary>
    public static string ServerUrl
    {
        get
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("KUROMON_MUSUBI_URL") is { Length: > 0 } test) return test.TrimEnd('/');
#endif
            return DefaultServerUrl;
        }
    }

    public static string DefaultPageUrl => $"{ServerUrl}/apps/{AppId}";

    /// <summary>Checks the latest release. <see cref="UpdateCheckResult.Update"/> is set when a newer version exists.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServerUrl}/api/version/{AppId}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Error, null, ex.Message);
        }

        using (response)
        {
            // 404: nothing published yet, which is "up to date", not an error.
            if ((int)response.StatusCode == 404) return new UpdateCheckResult(UpdateCheckStatus.Ok, null, null);
            if (!response.IsSuccessStatusCode) return new UpdateCheckResult(UpdateCheckStatus.Error, null, $"HTTP {(int)response.StatusCode}");

            VersionResponse? data;
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                data = await JsonSerializer.DeserializeAsync<VersionResponse>(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Error, null, ex.Message);
            }
            if (data is null || SemVer.TagToVersion(data.LatestVersion ?? "") is not { } latest)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Error, null, "invalid response");
            }
            if (!SemVer.IsNewer(latest, AppConfig.CurrentVersionText.TrimStart('v')))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Ok, null, null);
            }

            var fileUrl = IsHttps(data.FileUrl) ? data.FileUrl! : string.Empty;
            var sha = data.Sha256 is { Length: 64 } s && s.All(Uri.IsHexDigit) ? s.ToLowerInvariant() : null;
            var page = IsWeb(data.DownloadUrl) ? data.DownloadUrl! : DefaultPageUrl;
            var update = new UpdateInfo(latest, "v" + latest, data.ReleaseNotes ?? string.Empty, fileUrl, $"KuroMon-Setup-{latest}.exe", sha, page);
            return new UpdateCheckResult(UpdateCheckStatus.Ok, update, null);
        }
    }

    private static bool IsWeb(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>Installers are downloaded over https only (http just for a local test server in Debug builds).</summary>
    private static bool IsHttps(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme == Uri.UriSchemeHttps) return true;
#if DEBUG
        if (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) return true;
#endif
        return false;
    }

    /// <summary>Downloads the installer into %TEMP% and checks its SHA-256 (required). Returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!info.CanInstall) throw new InvalidOperationException("ダウンロード先か確認用の値がありません。");
        var dir = Path.Combine(Path.GetTempPath(), "KuroMon_update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, info.AssetName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, info.DownloadUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? 0;
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(target))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    done += read;
                    if (done > MaxInstallerBytes) throw new IOException("ダウンロードしたファイルが大きすぎます。");
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (total > 0) progress?.Report(done * 100d / total);
                }
            }

            await using (var fs = File.OpenRead(target))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
                if (!actual.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("ダウンロードしたファイルが壊れているか、改ざんされています。");
                }
            }
            return target;
        }
        catch
        {
            try { File.Delete(target); } catch { }
            throw;
        }
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

    /// <summary>Opens the download page (http / https only) in the default browser.</summary>
    public static void OpenPage(string url)
    {
        if (!IsWeb(url)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void Dispose() => _http.Dispose();

    private sealed class VersionResponse
    {
        [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
        [JsonPropertyName("file_url")] public string? FileUrl { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("release_notes")] public string? ReleaseNotes { get; set; }
    }
}
