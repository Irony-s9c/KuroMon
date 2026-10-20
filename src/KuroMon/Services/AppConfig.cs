using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace KuroMon.Services;

/// <summary>Application paths, version and privileges. KuroMon reads no .env file and holds no secrets.</summary>
public static class AppConfig
{
    /// <summary>Current app version (from &lt;Version&gt; in the .csproj).</summary>
    public static Version CurrentVersion { get; } = ReadVersion();

    public static string CurrentVersionText => $"v{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

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

    private static Version ReadVersion()
    {
        var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }
}
