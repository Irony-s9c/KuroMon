using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace KuroMon.Services;

/// <summary>
/// Auto start at logon through Task Scheduler ("run with highest privileges").
/// The registry Run key cannot start requireAdministrator apps, so a scheduled task is used instead.
/// </summary>
public static class AutoStartService
{
    public const string TaskName = "KuroMon";
    public const string MinimizedArgument = "--minimized";

    /// <summary>Registers or removes the task and stores the choice. Returns false when schtasks failed.</summary>
    public static bool SetEnabled(bool enable)
    {
        var ok = enable ? Register() : Unregister();
        if (ok)
        {
            AppSettings.Current.AutoStart = enable;
            AppSettings.Current.Save();
        }
        return ok;
    }

    public static bool IsRegistered() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Creates or overwrites the logon task so it points to the current exe path.</summary>
    public static bool Register()
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "KuroMon_task.xml");
        try
        {
            // Task Scheduler expects UTF-16 XML.
            File.WriteAllText(xmlPath, BuildTaskXml(), Encoding.Unicode);
            var code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", out var output);
            if (code != 0) AppLog.Warn($"AutoStart register failed ({code}): {output}");
            else AppLog.Info("AutoStart task registered: " + AppConfig.ExePath);
            return code == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("AutoStart register failed.", ex);
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* ignore */ }
        }
    }

    public static bool Unregister()
    {
        if (!IsRegistered()) return true;
        var code = RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out var output);
        if (code != 0) AppLog.Warn($"AutoStart unregister failed ({code}): {output}");
        else AppLog.Info("AutoStart task removed.");
        return code == 0;
    }

    private static string BuildTaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var exe = SecurityElement.Escape(AppConfig.ExePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(AppConfig.ExePath) ?? string.Empty);

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Start KuroMon hardware monitor at logon.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>{MinimizedArgument}</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            // schtasks writes messages in the console (OEM) code page, e.g. cp932 on Japanese Windows.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);

            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            })!;
            output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch (Exception ex)
        {
            output = ex.Message;
            return -1;
        }
    }
}
