using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KuroMon.Models;
using KuroMon.Services;

namespace KuroMon.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly HardwareMonitorService _monitor = new();
    private readonly DispatcherTimer _timer;
    private bool _sampling;

    public MainViewModel()
    {
        Cpu = new DeviceViewModel("CPU", Glyphs.Cpu, "使用率");
        Gpu = new DeviceViewModel("GPU", Glyphs.Gpu, "使用率");
        Ram = new DeviceViewModel("RAM", Glyphs.Ram, "使用率");
        Devices = [Cpu, Gpu, Ram];

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += async (_, _) => await SampleAsync();
        AppSettings.Changed += OnSettingsChanged;
    }

    public string Title => "KuroMon";
    public string VersionText => AppConfig.CurrentVersionText;
    public string PrivilegeText => AppConfig.IsAdministrator ? "管理者モード" : "標準ユーザー (一部の値を取得できません)";

    public DeviceViewModel Cpu { get; }
    public DeviceViewModel Gpu { get; }
    public DeviceViewModel Ram { get; }
    public IReadOnlyList<DeviceViewModel> Devices { get; }

    // Shortcuts used by the tray icon and the high temperature alert.
    public MetricViewModel CpuTemperature => Cpu.Temperature;
    public MetricViewModel CpuUsage => Cpu.Usage;
    public MetricViewModel GpuTemperature => Gpu.Temperature;
    public MetricViewModel GpuUsage => Gpu.Usage;
    public MetricViewModel MemoryUsage => Ram.Usage;

    public ObservableCollection<InfoSection> CpuSections { get; } = [];
    public ObservableCollection<InfoSection> GpuSections { get; } = [];
    public ObservableCollection<InfoSection> DisplaySections { get; } = [];
    public ObservableCollection<InfoSection> MemorySections { get; } = [];
    public ObservableCollection<MemoryModule> MemoryModules { get; } = [];
    public ObservableCollection<InfoSection> MotherboardSections { get; } = [];

    [ObservableProperty] private bool _isLoadingDetails = true;
    [ObservableProperty] private string _statusText = "センサーを初期化しています...";
    [ObservableProperty] private string _lastUpdatedText = string.Empty;
    [ObservableProperty] private bool _isDarkTheme = AppSettings.Current.DarkTheme;
    [ObservableProperty] private string? _notice;

    public ToastQueue Snackbar { get; } = new(TimeSpan.FromSeconds(2.5));

    /// <summary>Latest sample (used by the tray icon tooltip).</summary>
    public SensorSnapshot? LastSnapshot { get; private set; }

    /// <summary>Raised on the UI thread after every sample.</summary>
    public event Action? Sampled;

    public async Task InitializeAsync()
    {
        var detailsTask = Task.Run(HardwareInfoService.Collect);
        var openTask = Task.Run(_monitor.Open);

        try
        {
            await openTask;
            StatusText = "1 秒ごとに更新";
            RefreshPawnIoState();
            if (PawnIoMissing)
            {
                Notice = "CPU 温度の取得には PawnIO ドライバーが必要です。「インストール」を押すと KuroMon に同梱の PawnIO を導入して再起動します。";
            }
        }
        catch (Exception ex)
        {
            StatusText = "センサーの初期化に失敗しました: " + ex.Message;
            AppLog.Error("Sensor initialization failed.", ex);
        }

        await SampleAsync();
        _timer.Start();

        try
        {
            var details = await detailsTask;
            Fill(CpuSections, details.Cpu);
            Fill(GpuSections, details.Gpu);
            Fill(DisplaySections, details.Displays);
            Fill(MemorySections, details.Memory);
            Fill(MemoryModules, details.MemoryModules);
            Fill(MotherboardSections, details.Motherboard);
        }
        catch (Exception ex)
        {
            Snackbar.Enqueue("詳細情報の取得に失敗しました: " + ex.Message);
            AppLog.Error("Collecting hardware details failed.", ex);
        }
        finally
        {
            IsLoadingDetails = false;
        }
    }

    private async Task SampleAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var s = await Task.Run(_monitor.Sample);
            LastSnapshot = s;

            var inv = CultureInfo.InvariantCulture;
            Cpu.Update(s.CpuLoad, s.CpuTemperature, ShortCpuName(s.CpuName),
                s.CpuClockMhz is { } mhz ? $"最大クロック {(mhz / 1000).ToString("0.00", inv)} GHz" : null,
                PawnIoMissing ? "PawnIO が必要です" : "読み取れません");
            Gpu.Update(s.GpuLoad, s.GpuTemperature, s.GpuName ?? "GPU が見つかりません",
                s.GpuMemoryUsedMb is { } used && s.GpuMemoryTotalMb is { } total && total > 0
                    ? $"VRAM {(used / 1024).ToString("0.0", inv)} / {(total / 1024).ToString("0.0", inv)} GB"
                    : null,
                "読み取れません");
            Ram.Update(s.MemoryLoad, s.MemoryTemperature, $"物理メモリ {s.MemoryTotalGb.ToString("0", inv)} GB",
                $"使用中 {s.MemoryUsedGb.ToString("0.0", inv)} / {s.MemoryTotalGb.ToString("0.0", inv)} GB",
                "センサー非搭載");

            LastUpdatedText = "最終更新 " + DateTime.Now.ToString("HH:mm:ss", inv);
            Sampled?.Invoke();
            OnSampledForMemory(s);
        }
        catch (Exception ex)
        {
            StatusText = "取得エラー: " + ex.Message;
            AppLog.Error("Sampling failed.", ex);
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>"AMD Ryzen 7 7800X3D 8-Core Processor" -> "AMD Ryzen 7 7800X3D" (the full name is on the hardware tab).</summary>
    private static string? ShortCpuName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var s = System.Text.RegularExpressions.Regex.Replace(name, @"\((R|TM|tm)\)|\s+\d+-Core Processor|\s+Processor|\s+CPU\s+@.*$", string.Empty);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s{2,}", " ").Trim();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        Themes.ThemeManager.Apply(IsDarkTheme);
        AppSettings.Current.DarkTheme = IsDarkTheme;
        AppSettings.Current.Save();
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    [RelayCommand]
    private void Copy(string? tab)
    {
        var sb = new StringBuilder();
        void Append(IEnumerable<InfoSection> sections)
        {
            foreach (var section in sections)
            {
                sb.AppendLine($"[{section.Title}]");
                foreach (var item in section.Items) sb.AppendLine($"{item.Label}: {item.Value}");
                sb.AppendLine();
            }
        }

        switch (tab)
        {
            case "cpu": Append(CpuSections); break;
            case "gpu": Append(GpuSections); break;
            case "display": Append(DisplaySections); break;
            case "memory":
                Append(MemorySections);
                foreach (var m in MemoryModules)
                {
                    sb.AppendLine($"{m.Slot}: {m.Manufacturer} {m.PartNumber} {m.Capacity} {m.Type} {m.Speed} {m.FormFactor}");
                }
                break;
            case "board": Append(MotherboardSections); break;
            default:
                Append(CpuSections); Append(GpuSections); Append(DisplaySections); Append(MemorySections); Append(MotherboardSections);
                break;
        }

        try
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
            Snackbar.Enqueue("クリップボードにコピーしました");
        }
        catch
        {
            Snackbar.Enqueue("コピーに失敗しました");
        }
    }

    /// <summary>True while PawnIO is missing or too old; shows the install button.</summary>
    [ObservableProperty] private bool _pawnIoMissing;
    [ObservableProperty] private string _pawnIoStatusText = "-";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallPawnIoCommand))]
    private bool _isInstallingPawnIo;

    private void RefreshPawnIoState()
    {
        var version = PawnIoService.InstalledVersion;
        PawnIoMissing = PawnIoService.NeedsInstall;
        PawnIoStatusText = version is null
            ? "未インストール (CPU 温度を取得できません)"
            : PawnIoMissing ? $"{version.ToString(3)} (古いため更新が必要)" : $"{version.ToString(3)} (インストール済み)";
    }

    private bool CanInstallPawnIo() => !IsInstallingPawnIo;

    [RelayCommand(CanExecute = nameof(CanInstallPawnIo))]
    private async Task InstallPawnIo()
    {
        IsInstallingPawnIo = true;
        Notice = "PawnIO ドライバーをインストールしています...";
        try
        {
            var result = await PawnIoService.InstallAsync();
            RefreshPawnIoState();
            switch (result)
            {
                case PawnIoInstallResult.Installed:
                    // LibreHardwareMonitor loads the driver only when the sensors are opened, so restart KuroMon.
                    Notice = "PawnIO をインストールしました。KuroMon を再起動します...";
                    await Task.Delay(800);
                    App.Current.Restart();
                    break;
                case PawnIoInstallResult.RestartRequired:
                    Notice = "PawnIO をインストールしました。CPU 温度を表示するには Windows を再起動してください。";
                    break;
                default:
                    Notice = "PawnIO のインストールに失敗しました。詳細はログフォルダを確認してください。";
                    break;
            }
        }
        finally
        {
            IsInstallingPawnIo = false;
        }
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    public void Dispose()
    {
        AppSettings.Changed -= OnSettingsChanged;
        _timer.Stop();
        _monitor.Dispose();
    }
}
