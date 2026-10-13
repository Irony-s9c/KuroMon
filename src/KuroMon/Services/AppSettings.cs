using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KuroMon.Services;

/// <summary>Value shown as a number on the notification area icon.</summary>
public enum TrayMetric
{
    CpuTemperature,
    GpuTemperature,
    GpuUsage,
    MemoryUsage,
    CpuUsage,
}

/// <summary>User preferences stored in %LOCALAPPDATA%\KuroMon\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KuroMon", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>True when no settings file existed at startup (first launch).</summary>
    [JsonIgnore]
    public bool IsFirstRun { get; private set; }

    public bool AutoStart { get; set; } = true;
    public bool DarkTheme { get; set; } = true;
    public TrayMetric TrayMetric { get; set; } = TrayMetric.CpuTemperature;
    public bool TrayHintShown { get; set; }
    public bool HighTemperatureAlert { get; set; } = true;

    /// <summary>Release tag the user declined; automatic checks do not ask about it again.</summary>
    public string? SkippedVersion { get; set; }

    // ---- Overlay ----
    public bool OverlayEnabled { get; set; }
    /// <summary>Screen position in DIPs; null = top-left corner of the primary work area.</summary>
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
    /// <summary>Mouse clicks pass through the overlay to the game (it can no longer be dragged).</summary>
    public bool OverlayClickThrough { get; set; }

    // ---- Memory optimization ----
    /// <summary>Optimize automatically while a fullscreen game is in front.</summary>
    public bool MemoryAutoOptimize { get; set; }
    /// <summary>Memory usage (%) at or above which the automatic mode runs.</summary>
    public int MemoryAutoThreshold { get; set; } = 80;
    public bool MemoryPurgeStandby { get; set; } = true;
    /// <summary>Extra process names (without ".exe") that are never trimmed.</summary>
    public List<string> MemoryExclusions { get; set; } = [];

    /// <summary>Raised after settings are saved (on the calling thread).</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to read settings.json; using defaults.", ex);
        }
        Current = new AppSettings { IsFirstRun = true };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions), new UTF8Encoding(false));
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to save settings.json.", ex);
        }
    }
}
