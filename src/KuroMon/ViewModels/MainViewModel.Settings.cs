using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KuroMon.Services;

namespace KuroMon.ViewModels;

public sealed record TrayMetricOption(TrayMetric Value, string Label);

/// <summary>Settings tab. Values live in <see cref="AppSettings"/> so the tray menu and this tab stay in sync.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<TrayMetricOption> TrayMetricOptions { get; } =
    [
        new(TrayMetric.CpuTemperature, "CPU 温度"),
        new(TrayMetric.CpuUsage, "CPU 使用率"),
        new(TrayMetric.GpuTemperature, "GPU 温度"),
        new(TrayMetric.GpuUsage, "GPU 使用率"),
        new(TrayMetric.MemoryUsage, "メモリ使用率"),
    ];

    public bool AutoStartEnabled
    {
        get => AppSettings.Current.AutoStart;
        set
        {
            if (value == AppSettings.Current.AutoStart) return;
            if (!AutoStartService.SetEnabled(value))
            {
                Snackbar.Enqueue("自動起動の設定に失敗しました。管理者として起動しているか確認してください。");
            }
            OnPropertyChanged();
        }
    }

    public TrayMetricOption SelectedTrayMetric
    {
        get => TrayMetricOptions.First(o => o.Value == AppSettings.Current.TrayMetric);
        set
        {
            if (value is null || value.Value == AppSettings.Current.TrayMetric) return;
            AppSettings.Current.TrayMetric = value.Value;
            AppSettings.Current.Save();
        }
    }

    public bool HighTemperatureAlertEnabled
    {
        get => AppSettings.Current.HighTemperatureAlert;
        set
        {
            if (value == AppSettings.Current.HighTemperatureAlert) return;
            AppSettings.Current.HighTemperatureAlert = value;
            AppSettings.Current.Save();
        }
    }

    public string UpdateServerText => $"Musubi ({new Uri(UpdateService.ServerUrl).Host})";

    [ObservableProperty] private string _updateStatusText = "未確認";
    [ObservableProperty] private string _lastUpdateCheckText = "-";
    [ObservableProperty] private bool _updateStatusIsProblem;

    /// <summary>Reflects the latest update check in the settings tab.</summary>
    public void SetUpdateStatus(UpdateCheckResult r)
    {
        UpdateStatusText = r.Status == UpdateCheckStatus.Ok
            ? r.Update is null ? "正常 (最新バージョン)" : $"正常 (新しいバージョン {r.Update.TagName} があります)"
            : "確認に失敗 (" + r.Message + ")";
        UpdateStatusIsProblem = r.Status != UpdateCheckStatus.Ok;
        LastUpdateCheckText = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public string ExePathText => AppConfig.ExePath;

    public string LogDirectoryText => AppLog.Directory;

    [RelayCommand]
    private async Task CheckUpdate() => await App.Current.CheckForUpdateAsync(silent: false, automatic: false);

    [RelayCommand]
    private void OpenLogFolder() => TrayIconService.OpenLogFolder();

    private void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(AutoStartEnabled));
        OnPropertyChanged(nameof(SelectedTrayMetric));
        OnPropertyChanged(nameof(HighTemperatureAlertEnabled));
        OnMemorySettingsChanged();
    }
}
