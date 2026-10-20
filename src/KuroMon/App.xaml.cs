using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using KuroMon.Services;
using KuroMon.ViewModels;
using KuroMon.Views;

namespace KuroMon;

public partial class App : Application
{
    public const string AfterUpdateArgument = "--after-update";
    /// <summary>Started by <see cref="Restart"/>: wait for the previous instance like after an update.</summary>
    public const string RestartArgument = "--restart";

    private const string MutexName = @"Local\KuroMon.SingleInstance";
    private const string ShowEventName = @"Local\KuroMon.Show";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private MainViewModel? _vm;
    private MainWindow? _main;
    private MiniPanel? _panel;
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private DispatcherTimer? _updateTimer;
    private bool _exiting;
    private bool _updateDialogOpen;
    private bool _checkingUpdate;

    // Update check scheduling (Musubi server).
    private static readonly TimeSpan RegularCheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ErrorRetryInterval = TimeSpan.FromMinutes(30);

    // High temperature alert: notify after 10 consecutive samples (= 10 s) at or above the threshold.
    private const float AlertThreshold = 85f;
    private const int AlertSamples = 10;
    private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(10);
    private int _cpuHotSamples;
    private int _gpuHotSamples;
    private DateTime _lastAlert = DateTime.MinValue;

    public static new App Current => (App)Application.Current;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        AppLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => AppLog.Error("Unhandled exception.", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            AppLog.Error("Unobserved task exception.", ev.Exception);
            ev.SetObserved();
        };

        if (!AcquireSingleInstance(e.Args.Contains(AfterUpdateArgument) || e.Args.Contains(RestartArgument)))
        {
            AppLog.Info("Another instance is running; asked it to show its window.");
            Shutdown();
            return;
        }

        AppSettings.Load();
        if (AppSettings.Current.IsFirstRun) AppSettings.Current.Save();
        ApplyTheme(AppSettings.Current.DarkTheme);

        _vm = new MainViewModel();
        _main = new MainWindow(_vm);
        _main.Closing += OnMainClosing;

        _panel = new MiniPanel { DataContext = _vm };
        _panel.OpenMainRequested += ShowMain;

        AppSettings.Changed += SyncOverlay;

        _tray = new TrayIconService(_vm);
        _tray.OpenMainRequested += ShowMain;
        _tray.TogglePanelRequested += () => _panel.Toggle();
        _tray.ExitRequested += ExitApp;
        _vm.Sampled += OnSampled;

        // Windows shutdown / sign-out / installer (Restart Manager): really exit instead of hiding to the tray.
        SessionEnding += (_, _) =>
        {
            AppLog.Info("Session ending.");
            ExitApp();
        };

        StartShowListener();
        SyncOverlay();

        if (!e.Args.Contains(AutoStartService.MinimizedArgument)) ShowMain();

        _ = Task.Run(SyncAutoStart);

        await _vm.InitializeAsync();
        // The app stays resident: re-check every 6 hours (sooner after a failed check).
        _updateTimer = new DispatcherTimer { Interval = RegularCheckInterval };
        _updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(silent: true, automatic: true);

        await CheckForUpdateAsync(silent: true, automatic: true);
        _updateTimer.Start();
    }

    /// <summary>Updates status, notifications and retry timing from a check result.</summary>
    private void HandleCheckStatus(UpdateCheckResult r, bool silent)
    {
        _vm?.SetUpdateStatus(r);
        if (_updateTimer is not null)
        {
            var interval = r.Status == UpdateCheckStatus.Error ? ErrorRetryInterval : RegularCheckInterval;
            if (_updateTimer.Interval != interval) _updateTimer.Interval = interval;
        }
        if (r.Status == UpdateCheckStatus.Error)
        {
            AppLog.Warn("Update check failed: " + r.Message);
            if (!silent) _vm?.Snackbar.Enqueue("アップデートを確認できませんでした: " + r.Message);
        }
    }

    private void OnSampled()
    {
        _tray?.Update();
        CheckTemperatureAlert();
    }

    private void CheckTemperatureAlert()
    {
        var s = _vm?.LastSnapshot;
        if (s is null || _tray is null) return;

        _cpuHotSamples = s.CpuTemperature >= AlertThreshold ? _cpuHotSamples + 1 : 0;
        _gpuHotSamples = s.GpuTemperature >= AlertThreshold ? _gpuHotSamples + 1 : 0;

        if (!AppSettings.Current.HighTemperatureAlert) return;
        if (DateTime.Now - _lastAlert < AlertCooldown) return;

        string? message = null;
        if (_cpuHotSamples >= AlertSamples) message = $"CPU 温度が {s.CpuTemperature:0} °C になっています。";
        else if (_gpuHotSamples >= AlertSamples) message = $"GPU 温度が {s.GpuTemperature:0} °C になっています。";
        if (message is null) return;

        _lastAlert = DateTime.Now;
        AppLog.Warn("High temperature alert: " + message);
        _tray.ShowBalloon("KuroMon - 高温警告", message + $"（{AlertThreshold:0} °C 以上が {AlertSamples} 秒継続）");
    }

    /// <summary>Shows, hides or updates the overlay to match the settings.</summary>
    private void SyncOverlay()
    {
        if (_exiting || _vm is null) return;

        if (!AppSettings.Current.OverlayEnabled)
        {
            _overlay?.Close();
            _overlay = null;
            return;
        }

        if (_overlay is null)
        {
            _overlay = new OverlayWindow { DataContext = _vm };
            _overlay.Show();
        }
        _overlay.ApplyClickThrough(AppSettings.Current.OverlayClickThrough);
    }

    public void ShowTrayBalloon(string title, string message) => _tray?.ShowBalloon(title, message);

    public void ShowMain()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;  // bring to front even when another app has focus
        _main.Topmost = false;
        _main.Focus();
    }

    /// <summary>Starts a new instance (keeping the window visible) and exits this one.</summary>
    public void Restart()
    {
        try
        {
            AppLog.Info("Restarting.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppConfig.ExePath, RestartArgument)
            {
                UseShellExecute = true,
                WorkingDirectory = AppConfig.ExeDirectory,
            });
            ExitApp();
        }
        catch (Exception ex)
        {
            AppLog.Error("Restart failed.", ex);
            _vm?.Snackbar.Enqueue("再起動に失敗しました。KuroMon を手動で起動し直してください。");
        }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Info("Exit requested.");

        _updateTimer?.Stop();
        _tray?.Dispose();
        _tray = null;
        AppSettings.Changed -= SyncOverlay;
        _overlay?.Close();
        _overlay = null;
        _vm?.Dispose();
        _panel?.Close();
        _main?.Close();
        Shutdown();
    }

    /// <param name="silent">Do not report "up to date" / errors.</param>
    /// <param name="automatic">Started by startup or timer: skip the version the user declined before.</param>
    public async Task CheckForUpdateAsync(bool silent, bool automatic)
    {
        if (_vm is null || _updateDialogOpen || _checkingUpdate) return;

        UpdateCheckResult result;
        _checkingUpdate = true;
        try
        {
            using var service = new UpdateService();
            result = await service.CheckAsync();
        }
        catch (Exception ex)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Error, null, ex.Message);
        }
        finally
        {
            _checkingUpdate = false;
        }

        HandleCheckStatus(result, silent);
        if (result.Status != UpdateCheckStatus.Ok) return;

        var info = result.Update;
        if (info is null)
        {
            if (!silent) _vm.Snackbar.Enqueue($"最新バージョンです ({AppConfig.CurrentVersionText})");
            return;
        }

        if (automatic && string.Equals(info.TagName, AppSettings.Current.SkippedVersion, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info($"Update {info.TagName} was skipped by the user; not asking again automatically.");
            return;
        }

        AppLog.Info($"Update available: {info.TagName}");
        var dialog = new UpdateWindow(info);
        if (_main is { IsVisible: true })
        {
            dialog.Owner = _main;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        bool accepted;
        _updateDialogOpen = true;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            _updateDialogOpen = false;
        }

        if (!accepted)
        {
            AppSettings.Current.SkippedVersion = info.TagName;
            AppSettings.Current.Save();
            AppLog.Info($"User declined update {info.TagName}.");
            return;
        }

        if (dialog.DownloadedPath is { } path)
        {
            try
            {
                UpdateService.RunInstaller(path);
                AppLog.Info($"Installer started for {info.TagName}.");
                ExitApp();
            }
            catch (Exception ex)
            {
                AppLog.Error("Applying update failed.", ex);
                MessageBox.Show("アップデートの適用に失敗しました。\n" + ex.Message, "KuroMon",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnMainClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting) return;

        // The close button hides the window; the app keeps running in the notification area.
        e.Cancel = true;
        _main?.Hide();

        if (!AppSettings.Current.TrayHintShown)
        {
            _tray?.ShowBalloon("KuroMon", "通知領域で動作を続けています。終了するにはアイコンを右クリックして「終了」を選んでください。");
            AppSettings.Current.TrayHintShown = true;
            AppSettings.Current.Save();
        }
    }

    private bool AcquireSingleInstance(bool afterUpdate)
    {
        _mutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex && afterUpdate)
        {
            // The previous version is still shutting down; wait for it.
            try { _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { _ownsMutex = true; }
        }

        if (_ownsMutex) return true;

        try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
        catch { /* first instance may be starting up */ }
        return false;
    }

    private void StartShowListener()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { _showEvent.WaitOne(); }
                catch { return; }
                if (_exiting) return;
                Dispatcher.BeginInvoke(ShowMain);
            }
        })
        { IsBackground = true, Name = "KuroMon.ShowListener" };
        thread.Start();
    }

    private static void SyncAutoStart()
    {
        try
        {
            if (AppSettings.Current.AutoStart)
            {
                // Re-register every launch so the task always points at the current exe location.
                if (!AutoStartService.Register()) AppLog.Warn("AutoStart is enabled but the task could not be registered.");
            }
            else
            {
                AutoStartService.Unregister();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("AutoStart sync failed.", ex);
        }
    }

    private static void ApplyTheme(bool dark) => Themes.ThemeManager.Apply(dark);

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception.", e.Exception);
        MessageBox.Show(e.Exception.Message + "\n\n詳細はログフォルダを確認してください。", "KuroMon - Error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch { /* ignore */ }
        }
        _mutex?.Dispose();
        AppLog.Info("KuroMon exited.");
        base.OnExit(e);
    }
}
