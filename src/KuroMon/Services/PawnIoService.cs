using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace KuroMon.Services;

public enum PawnIoInstallResult
{
    Installed,
    /// <summary>Installed, but Windows must be restarted before the driver works.</summary>
    RestartRequired,
    Failed,
}

/// <summary>
/// Detects and installs the PawnIO driver that LibreHardwareMonitor needs for CPU temperature.
/// The official PawnIO_setup.exe (pinned version) is embedded in KuroMon.exe, so no separate download is needed.
/// </summary>
public static class PawnIoService
{
    // Keep in sync with PawnIoVersion / PawnIoSha256 in KuroMon.csproj.
    public const string BundledVersion = "2.2.0";
    private const string BundledSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032";
    private const string ResourceName = "KuroMon.PawnIO_setup.exe";

    /// <summary>LibreHardwareMonitorLib 0.9.6 needs PawnIO 2.0 or newer.</summary>
    private static readonly Version MinimumVersion = new(2, 0, 0);

    private const int ErrorSuccessRebootRequired = 3010;
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

    /// <summary>Installed PawnIO version, or null. Read from the registry on every call (LibreHardwareMonitor caches it at startup).</summary>
    public static Version? InstalledVersion
    {
        get
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = hklm.OpenSubKey(UninstallKey);
                return Version.TryParse(key?.GetValue("DisplayVersion") as string, out var v) ? v : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>True when PawnIO is missing or too old for the bundled LibreHardwareMonitorLib.</summary>
    public static bool NeedsInstall => InstalledVersion is not { } v || v < MinimumVersion;

    /// <summary>Runs the bundled installer silently. Requires administrator rights (KuroMon always has them).</summary>
    public static async Task<PawnIoInstallResult> InstallAsync()
    {
        // %ProgramData%\KuroMon\pawnio with a unique name: CreateNew prevents reusing a file planted by another user.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KuroMon", "pawnio");
        var path = Path.Combine(dir, $"PawnIO_setup_{Guid.NewGuid():N}.exe");

        try
        {
            Directory.CreateDirectory(dir);
            await using (var resource = typeof(PawnIoService).Assembly.GetManifestResourceStream(ResourceName)
                                        ?? throw new InvalidOperationException("The PawnIO installer is not embedded in this build."))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await resource.CopyToAsync(file);
            }

            // Keep a read-only share open until the installer exits so nobody can swap the file after the hash check.
            await using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(guard));
            if (!hash.Equals(BundledSha256, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Error($"PawnIO installer hash mismatch: {hash}");
                return PawnIoInstallResult.Failed;
            }

            AppLog.Info($"Installing PawnIO {BundledVersion} (installed: {InstalledVersion?.ToString() ?? "none"}).");
            using var process = Process.Start(new ProcessStartInfo(path, "-install -silent")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Could not start the PawnIO installer.");
            await process.WaitForExitAsync();

            var code = process.ExitCode;
            AppLog.Info($"PawnIO installer exited with code {code}.");
            return code switch
            {
                0 => PawnIoInstallResult.Installed,
                ErrorSuccessRebootRequired => PawnIoInstallResult.RestartRequired,
                _ => PawnIoInstallResult.Failed,
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("PawnIO installation failed.", ex);
            return PawnIoInstallResult.Failed;
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
