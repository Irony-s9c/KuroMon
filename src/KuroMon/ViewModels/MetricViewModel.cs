using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KuroMon.ViewModels;

public enum MetricKind
{
    Temperature,
    Percent,
}

public enum MetricLevel
{
    Unknown,
    Normal,
    Warning,
    Critical,
}

/// <summary>One dashboard card: current value, sub text and a 60-second history.</summary>
public sealed partial class MetricViewModel : ObservableObject
{
    public const int HistoryLength = 60;

    private readonly Queue<double> _samples = new();

    public MetricViewModel(string title, string icon, MetricKind kind)
    {
        Title = title;
        Icon = icon;
        Kind = kind;
        Unit = kind == MetricKind.Temperature ? "°C" : "%";
    }

    public string Title { get; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Icon { get; }
    public MetricKind Kind { get; }
    public string Unit { get; }

    /// <summary>Upper bound of the history chart.</summary>
    public double ChartMax => Kind == MetricKind.Temperature ? 110 : 100;

    [ObservableProperty] private string _valueText = "--";
    /// <summary>Whole number for compact views (overlay, mini panel).</summary>
    [ObservableProperty] private string _shortText = "--";
    [ObservableProperty] private string _subText = string.Empty;
    [ObservableProperty] private double _gaugeValue;
    [ObservableProperty] private MetricLevel _level = MetricLevel.Unknown;
    [ObservableProperty] private IReadOnlyList<double> _history = [];
    [ObservableProperty] private bool _isAvailable;

    public void Push(float? value, string? subText = null)
    {
        if (subText is not null) SubText = subText;

        if (value is null || float.IsNaN(value.Value))
        {
            IsAvailable = false;
            ValueText = "N/A";
            ShortText = "--";
            GaugeValue = 0;
            Level = MetricLevel.Unknown;
            return;
        }

        var v = Math.Round(value.Value, 1);
        IsAvailable = true;
        ValueText = Kind == MetricKind.Temperature
            ? v.ToString("0.0", CultureInfo.InvariantCulture)
            : v.ToString("0", CultureInfo.InvariantCulture);
        ShortText = Math.Round(v).ToString("0", CultureInfo.InvariantCulture);
        // Gauge in % of the chart range, so temperatures use the same 0-110 °C scale as the chart.
        GaugeValue = Math.Clamp(v / ChartMax * 100d, 0, 100);
        Level = Classify(v);

        _samples.Enqueue(v);
        while (_samples.Count > HistoryLength) _samples.Dequeue();
        History = _samples.ToArray();
    }

    private MetricLevel Classify(double v) => Kind switch
    {
        MetricKind.Temperature => v >= 85 ? MetricLevel.Critical : v >= 70 ? MetricLevel.Warning : MetricLevel.Normal,
        _ => v >= 90 ? MetricLevel.Critical : v >= 75 ? MetricLevel.Warning : MetricLevel.Normal,
    };
}
