using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace KuroMon.Services;

/// <summary>
/// Application settings. Secrets are never hard-coded: they come from Windows environment variables
/// or from a ".env" file placed next to KuroMon.exe (environment variables win).
/// Values are re-read on every access, so a renewed token takes effect without restarting the app.
/// </summary>
public static class AppConfig
{
    public const string UpdateRepoKey = "KUROMON_UPDATE_REPO";
    public const string GitHubTokenKey = "KUROMON_GITHUB_TOKEN";
    public const string DotEnvFileName = ".env";

    private static readonly object DotEnvLock = new();
    private static Dictionary<string, string> _dotEnv = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _dotEnvStamp = DateTime.MinValue;

    public static string DotEnvPath => Path.Combine(AppContext.BaseDirectory, DotEnvFileName);

    /// <summary>Current app version (from &lt;Version&gt; in the .csproj).</summary>
    public static Version CurrentVersion { get; } = ReadVersion();

    public static string CurrentVersionText => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    /// <summary>Public release-only repository (no source code), used when KUROMON_UPDATE_REPO is not set.</summary>
    public const string DefaultUpdateRepository = "Irony-s9c/KuroMon";

    /// <summary>Release repository in "owner/repo" form (.env / environment variable, else the default).</summary>
    public static string UpdateRepository => Get(UpdateRepoKey) ?? DefaultUpdateRepository;

    /// <summary>Optional GitHub token (only needed for a private release repository).</summary>
    public static string? GitHubToken => Get(GitHubTokenKey);

    public static string ExeDirectory => AppContext.BaseDirectory;

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(ExeDirectory, "KuroMon.exe");

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    private static string? Get(string key)
    {
        // User / machine variables are read from the registry so changes apply without a restart;
        // the process block is only a snapshot taken at launch.
        var value = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User);
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value)) CurrentDotEnv().TryGetValue(key, out value);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Returns the .env contents, reloading the file when its timestamp changed.</summary>
    private static Dictionary<string, string> CurrentDotEnv()
    {
        lock (DotEnvLock)
        {
            DateTime stamp;
            try { stamp = File.Exists(DotEnvPath) ? File.GetLastWriteTimeUtc(DotEnvPath) : DateTime.MinValue; }
            catch { return _dotEnv; }

            if (stamp != _dotEnvStamp && LoadDotEnv() is { } loaded)
            {
                _dotEnv = loaded;
                _dotEnvStamp = stamp;
            }
            return _dotEnv;
        }
    }

    private static Version ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>Parses .env. Returns null when the file could not be read (e.g. an editor is still writing it).</summary>
    private static Dictionary<string, string>? LoadDotEnv()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = DotEnvPath;
            if (!File.Exists(path)) return result;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                {
                    value = value[1..^1];
                }
                else
                {
                    // Inline comment: KEY=value   # note
                    var hash = value.IndexOf(" #", StringComparison.Ordinal);
                    if (hash < 0) hash = value.IndexOf("\t#", StringComparison.Ordinal);
                    if (hash >= 0) value = value[..hash].Trim();
                }
                result[key] = value;
            }
            return result;
        }
        catch
        {
            // A broken or locked .env must not break the app; keep the previous values.
            return null;
        }
    }
}
