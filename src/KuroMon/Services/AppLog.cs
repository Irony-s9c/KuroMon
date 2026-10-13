using System.IO;
using System.Text;

namespace KuroMon.Services;

/// <summary>Simple daily log file under %LOCALAPPDATA%\KuroMon\logs (kept for 14 days).</summary>
public static class AppLog
{
    private const int RetentionDays = 14;
    private static readonly object Sync = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuroMon", "logs");

    public static void Initialize()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var file in new DirectoryInfo(Directory).GetFiles("kuromon_*.log"))
            {
                if (file.LastWriteTime < DateTime.Now.AddDays(-RetentionDays)) file.Delete();
            }
        }
        catch
        {
            // Logging must never break the app.
        }
        Info($"KuroMon {AppConfig.CurrentVersionText} started (admin={AppConfig.IsAdministrator}, args={string.Join(' ', Environment.GetCommandLineArgs().Skip(1))})");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            var path = Path.Combine(Directory, $"kuromon_{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (Sync)
            {
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // ignore
        }
    }
}
