using System.Runtime.InteropServices;
using KuroMon.Models;
using LibreHardwareMonitor.Hardware;

namespace KuroMon.Services;

/// <summary>
/// Real-time sensor sampling backed by LibreHardwareMonitorLib.
/// CPU temperature needs the PawnIO driver; GPU values come from vendor APIs (NVAPI / ADL / D3D).
/// </summary>
public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        // DIMM temperature (SPD thermal sensor on DDR4 / DDR5). Physical memory load still comes from GlobalMemoryStatusEx.
        IsMemoryEnabled = true,
        IsMotherboardEnabled = false,
    };

    private readonly object _lock = new();
    private bool _opened;

    public void Open()
    {
        lock (_lock)
        {
            if (_opened) return;
            _computer.Open();
            _opened = true;
        }
    }

    public SensorSnapshot Sample()
    {
        float? cpuTemp = null, cpuLoad = null, cpuClock = null, gpuLoad = null, gpuTemp = null, gpuMemUsed = null, gpuMemTotal = null, memTemp = null;
        string? gpuName = null, cpuName = null;

        lock (_lock)
        {
            if (_opened)
            {
                foreach (var hw in _computer.Hardware)
                {
                    try
                    {
                        hw.Update();
                        foreach (var sub in hw.SubHardware) sub.Update();
                    }
                    catch { /* a single failing device must not stop the loop */ }
                }

                var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (cpu is not null)
                {
                    cpuName = cpu.Name;
                    cpuTemp = PickCpuTemperature(cpu);
                    cpuLoad = Find(cpu, SensorType.Load, "CPU Total");
                    cpuClock = cpu.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core #", StringComparison.Ordinal) && IsValid(s) && s.Value > 0)
                                          .Select(s => s.Value)
                                          .DefaultIfEmpty(null)
                                          .Max();
                }

                var gpu = PickGpu(_computer.Hardware);
                if (gpu is not null)
                {
                    gpuName = gpu.Name;
                    gpuLoad = Find(gpu, SensorType.Load, "GPU Core", "D3D 3D") ?? First(gpu, SensorType.Load);
                    gpuTemp = Find(gpu, SensorType.Temperature, "GPU Core", "GPU Hot Spot") ?? First(gpu, SensorType.Temperature);
                    gpuMemUsed = Find(gpu, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                    gpuMemTotal = Find(gpu, SensorType.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total");
                }

                memTemp = PickDimmTemperature(_computer.Hardware);
            }
        }

        var (memLoad, usedGb, totalGb) = ReadMemoryStatus();
        return new SensorSnapshot(cpuTemp, cpuLoad, memLoad, usedGb, totalGb, gpuLoad, gpuTemp, gpuName)
        {
            CpuName = cpuName,
            CpuClockMhz = cpuClock,
            MemoryTemperature = memTemp,
            GpuMemoryUsedMb = gpuMemUsed,
            GpuMemoryTotalMb = gpuMemTotal,
        };
    }

    /// <summary>Hottest "DIMM #n" sensor of any memory device (modules without a thermal sensor report nothing).</summary>
    private static float? PickDimmTemperature(IEnumerable<IHardware> hardware)
    {
        static IEnumerable<IHardware> Flatten(IHardware h) => h.SubHardware.SelectMany(Flatten).Prepend(h);

        return hardware.Where(h => h.HardwareType == HardwareType.Memory)
                       .SelectMany(Flatten)
                       .SelectMany(h => h.Sensors)
                       .Where(s => s.SensorType == SensorType.Temperature && s.Name.StartsWith("DIMM #", StringComparison.Ordinal) && IsValid(s))
                       .Select(s => s.Value)
                       .DefaultIfEmpty(null)
                       .Max();
    }

    private static float? PickCpuTemperature(IHardware cpu)
    {
        // Intel: "CPU Package" / AMD: "Core (Tctl/Tdie)" / fallback: average or hottest core.
        return Find(cpu, SensorType.Temperature, "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "Core Average")
               ?? cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0)
                             .Select(s => s.Value)
                             .DefaultIfEmpty(null)
                             .Max();
    }

    private static IHardware? PickGpu(IEnumerable<IHardware> hardware)
    {
        // Prefer discrete GPUs over integrated graphics.
        var gpus = hardware.Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        return gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
               ?? gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
               ?? gpus.FirstOrDefault();
    }

    private static float? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var sensor = hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name == name && IsValid(s));
            if (sensor is not null) return sensor.Value;
        }
        return null;
    }

    private static float? First(IHardware hw, SensorType type) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && IsValid(s))?.Value;

    // Without the PawnIO driver, CPU temperature sensors exist but report 0.
    private static bool IsValid(ISensor s) =>
        s.Value is { } v && !float.IsNaN(v) && (s.SensorType != SensorType.Temperature || v > 0);

    private static (float? Load, double UsedGb, double TotalGb) ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (null, 0, 0);

        const double gb = 1024d * 1024d * 1024d;
        double total = status.ullTotalPhys / gb;
        double used = (status.ullTotalPhys - status.ullAvailPhys) / gb;
        float load = total > 0 ? (float)(used / total * 100d) : 0f;
        return (load, used, total);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_opened)
            {
                try { _computer.Close(); } catch { /* ignore on shutdown */ }
                _opened = false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
