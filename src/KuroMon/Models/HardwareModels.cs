namespace KuroMon.Models;

/// <summary>One label/value row shown on a detail tab.</summary>
public sealed record InfoItem(string Label, string Value);

/// <summary>A titled group of rows (e.g. one GPU adapter).</summary>
public sealed record InfoSection(string Title, IReadOnlyList<InfoItem> Items);

/// <summary>One installed memory module (DIMM / SO-DIMM).</summary>
public sealed record MemoryModule(
    string Slot,
    string Manufacturer,
    string PartNumber,
    string Capacity,
    string Type,
    string Speed,
    string FormFactor);

/// <summary>Static hardware details collected once at startup.</summary>
public sealed class HardwareDetails
{
    public IReadOnlyList<InfoSection> Cpu { get; init; } = [];
    public IReadOnlyList<InfoSection> Gpu { get; init; } = [];
    public IReadOnlyList<InfoSection> Displays { get; init; } = [];
    public IReadOnlyList<InfoSection> Memory { get; init; } = [];
    public IReadOnlyList<MemoryModule> MemoryModules { get; init; } = [];
    public IReadOnlyList<InfoSection> Motherboard { get; init; } = [];
}

/// <summary>Values sampled on every monitoring tick. Null means "not available".</summary>
public sealed record SensorSnapshot(
    float? CpuTemperature,
    float? CpuLoad,
    float? MemoryLoad,
    double MemoryUsedGb,
    double MemoryTotalGb,
    float? GpuLoad,
    float? GpuTemperature,
    string? GpuName)
{
    public string? CpuName { get; init; }
    /// <summary>Highest core clock in MHz (needs PawnIO).</summary>
    public float? CpuClockMhz { get; init; }
    /// <summary>Hottest DIMM (DDR4 / DDR5 modules with an SPD thermal sensor only).</summary>
    public float? MemoryTemperature { get; init; }
    public float? GpuMemoryUsedMb { get; init; }
    public float? GpuMemoryTotalMb { get; init; }
}
