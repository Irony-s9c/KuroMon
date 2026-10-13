using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KuroMon.Models;
using KuroMon.Services;

namespace KuroMon.ViewModels;

/// <summary>Memory optimization tab, game detection and the overlay switch.</summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan AutoCooldown = TimeSpan.FromMinutes(3);
    private const int DetectEverySamples = 5;

    private int _detectCounter;
    private DetectedGame? _game;
    private DateTime _lastAutoRun = DateTime.MinValue;

    public IReadOnlyList<int> MemoryThresholdOptions { get; } = [60, 70, 75, 80, 85, 90];

    public string BuiltInExclusionsText => string.Join(", ", MemoryOptimizerService.BuiltInExclusions.Order(StringComparer.OrdinalIgnoreCase));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OptimizeMemoryCommand))]
    private bool _isOptimizing;

    [ObservableProperty] private string _lastOptimizeText = "まだ実行していません";
    [ObservableProperty] private string? _lastOptimizeDetail;
    [ObservableProperty] private string _gameStatusText = "ゲームを検出していません";
    [ObservableProperty] private bool _gameDetected;
    [ObservableProperty] private string _autoStatusText = string.Empty;

    public bool MemoryAutoOptimize
    {
        get => AppSettings.Current.MemoryAutoOptimize;
        set
        {
            if (value == AppSettings.Current.MemoryAutoOptimize) return;
            AppSettings.Current.MemoryAutoOptimize = value;
            AppSettings.Current.Save();
            UpdateAutoStatus(LastSnapshot);
        }
    }

    public int SelectedMemoryThreshold
    {
        get => AppSettings.Current.MemoryAutoThreshold;
        set
        {
            if (value == AppSettings.Current.MemoryAutoThreshold || value <= 0) return;
            AppSettings.Current.MemoryAutoThreshold = value;
            AppSettings.Current.Save();
            UpdateAutoStatus(LastSnapshot);
        }
    }

    public bool MemoryPurgeStandby
    {
        get => AppSettings.Current.MemoryPurgeStandby;
        set
        {
            if (value == AppSettings.Current.MemoryPurgeStandby) return;
            AppSettings.Current.MemoryPurgeStandby = value;
            AppSettings.Current.Save();
        }
    }

    /// <summary>User exclusions as editable text (comma, space or newline separated; ".exe" is optional).</summary>
    public string MemoryExclusionsText
    {
        get => string.Join(", ", AppSettings.Current.MemoryExclusions);
        set
        {
            var list = (value ?? string.Empty)
                .Split([',', '\n', '\r', ' ', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (list.SequenceEqual(AppSettings.Current.MemoryExclusions, StringComparer.OrdinalIgnoreCase)) return;
            AppSettings.Current.MemoryExclusions = list;
            AppSettings.Current.Save();
            OnPropertyChanged();
        }
    }

    public bool OverlayEnabled
    {
        get => AppSettings.Current.OverlayEnabled;
        set
        {
            if (value == AppSettings.Current.OverlayEnabled) return;
            AppSettings.Current.OverlayEnabled = value;
            AppSettings.Current.Save();
        }
    }

    public bool OverlayClickThrough
    {
        get => AppSettings.Current.OverlayClickThrough;
        set
        {
            if (value == AppSettings.Current.OverlayClickThrough) return;
            AppSettings.Current.OverlayClickThrough = value;
            AppSettings.Current.Save();
        }
    }

    [RelayCommand]
    private void ToggleOverlay() => OverlayEnabled = !OverlayEnabled;

    private bool CanOptimizeMemory() => !IsOptimizing;

    [RelayCommand(CanExecute = nameof(CanOptimizeMemory))]
    private async Task OptimizeMemory() => await RunOptimizeAsync(automatic: false);

    private async Task RunOptimizeAsync(bool automatic)
    {
        if (IsOptimizing) return;
        IsOptimizing = true;
        try
        {
            // Protect the game even when the optimization is started from KuroMon's own window.
            if (_game is { } g && !GameDetector.IsAlive(g.ProcessId)) _game = null;

            var options = new MemoryOptimizeOptions(
                AppSettings.Current.MemoryPurgeStandby,
                new HashSet<string>(AppSettings.Current.MemoryExclusions, StringComparer.OrdinalIgnoreCase),
                _game?.ProcessId);

            var r = await MemoryOptimizerService.OptimizeAsync(options, automatic);
            if (r is null) return;

            ShowResult(r);
            if (!automatic) Snackbar.Enqueue(LastOptimizeText);
        }
        catch (Exception ex)
        {
            AppLog.Error("Memory optimization failed.", ex);
            LastOptimizeText = "メモリの解放に失敗しました: " + ex.Message;
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    private void ShowResult(MemoryOptimizeResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        static string Gb(double bytes) => (bytes / 1073741824d).ToString("0.00", CultureInfo.InvariantCulture);
        double gained = (double)r.AvailableAfter - r.AvailableBefore;

        LastOptimizeText = $"{r.Time.ToString("HH:mm:ss", inv)} {(r.Automatic ? "自動" : "手動")}実行: " +
                           $"利用可能メモリ {Gb(r.AvailableBefore)} → {Gb(r.AvailableAfter)} GB ({(gained >= 0 ? "+" : "")}{Gb(gained)} GB)";
        LastOptimizeDetail = $"{r.ProcessesTrimmed} 個のプロセスの作業セットを {Gb(r.WorkingSetReleasedBytes)} GB 縮小 / " +
                             $"保護・対象外 {r.ProcessesSkipped} 個 / スタンバイリスト: {(r.StandbyPurged ? "解放" : "解放なし")}" +
                             (r.ProtectedGame is { } game ? $" / ゲーム {game} は対象外" : string.Empty);
    }

    /// <summary>Called after every sample: game detection every 5 s and the automatic mode.</summary>
    private void OnSampledForMemory(SensorSnapshot s)
    {
        if (++_detectCounter < DetectEverySamples) return;
        _detectCounter = 0;

        var game = GameDetector.DetectForegroundFullscreen();
        if (game is not null) _game = game;
        else if (_game is { } g && !GameDetector.IsAlive(g.ProcessId)) _game = null;

        GameDetected = game is not null;
        GameStatusText = game is not null
            ? $"ゲームを検出中: {game.ProcessName}（このプロセスは解放の対象外）"
            : _game is not null ? $"直前のゲーム: {_game.ProcessName}（対象外として保護中）" : "ゲームを検出していません（全画面のアプリが前面にあると検出します）";

        if (MemoryAutoOptimize && game is not null && s.MemoryLoad is { } load
            && load >= AppSettings.Current.MemoryAutoThreshold
            && DateTime.Now - _lastAutoRun >= AutoCooldown && !IsOptimizing)
        {
            _lastAutoRun = DateTime.Now;
            _ = RunOptimizeAsync(automatic: true);
        }
        UpdateAutoStatus(s);
    }

    private void UpdateAutoStatus(SensorSnapshot? s)
    {
        if (!MemoryAutoOptimize)
        {
            AutoStatusText = "自動最適化はオフです";
            return;
        }

        var load = s?.MemoryLoad is { } l ? $"{l:0}%" : "N/A";
        var threshold = AppSettings.Current.MemoryAutoThreshold;
        var wait = AutoCooldown - (DateTime.Now - _lastAutoRun);
        AutoStatusText = !GameDetected
            ? $"待機中: ゲームの起動を待っています（現在 {load} / しきい値 {threshold}%）"
            : wait > TimeSpan.Zero
                ? $"ゲーム中: 次の自動実行まで最短 {Math.Ceiling(wait.TotalMinutes):0} 分（現在 {load} / しきい値 {threshold}%）"
                : $"ゲーム中: メモリ使用率が {threshold}% 以上になると自動で解放します（現在 {load}）";
    }

    private void OnMemorySettingsChanged()
    {
        OnPropertyChanged(nameof(MemoryAutoOptimize));
        OnPropertyChanged(nameof(SelectedMemoryThreshold));
        OnPropertyChanged(nameof(MemoryPurgeStandby));
        OnPropertyChanged(nameof(MemoryExclusionsText));
        OnPropertyChanged(nameof(OverlayEnabled));
        OnPropertyChanged(nameof(OverlayClickThrough));
    }
}
