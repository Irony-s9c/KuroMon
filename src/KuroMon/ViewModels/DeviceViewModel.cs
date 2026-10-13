using CommunityToolkit.Mvvm.ComponentModel;

namespace KuroMon.ViewModels;

/// <summary>One dashboard panel (CPU / GPU / RAM): usage and temperature side by side.</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(string name, string icon, string usageLabel)
    {
        Name = name;
        Icon = icon;
        UsageLabel = usageLabel;
        Usage = new MetricViewModel($"{name} 使用率", icon, MetricKind.Percent);
        Temperature = new MetricViewModel($"{name} 温度", Glyphs.Temperature, MetricKind.Temperature);
    }

    public string Name { get; }
    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Icon { get; }
    public string UsageLabel { get; }
    public MetricViewModel Usage { get; }
    public MetricViewModel Temperature { get; }

    /// <summary>Model name (e.g. "AMD Ryzen 7 7800X3D").</summary>
    [ObservableProperty] private string _model = string.Empty;
    /// <summary>Extra figure under the panel (clock, VRAM, used / total GB).</summary>
    [ObservableProperty] private string? _detail;
    /// <summary>Shown under the temperature when it cannot be read (e.g. RAM without a sensor).</summary>
    [ObservableProperty] private string _temperatureHint = string.Empty;
    /// <summary>The worse of usage and temperature: drives the panel's status chip.</summary>
    [ObservableProperty] private MetricLevel _level = MetricLevel.Unknown;

    public void Update(float? usage, float? temperature, string? model, string? detail, string unavailableHint)
    {
        Usage.Push(usage);
        Temperature.Push(temperature);
        if (!string.IsNullOrEmpty(model)) Model = model;
        Detail = detail;
        TemperatureHint = Temperature.IsAvailable ? string.Empty : unavailableHint;
        Level = (MetricLevel)Math.Max((int)Usage.Level, (int)Temperature.Level);
    }
}
