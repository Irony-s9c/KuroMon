using System.Globalization;
using System.Management;
using KuroMon.Models;
using Microsoft.Win32;

namespace KuroMon.Services;

/// <summary>Static hardware details collected through WMI (plus the registry for accurate VRAM size).</summary>
public static class HardwareInfoService
{
    private const string NotAvailable = "N/A";

    public static HardwareDetails Collect()
    {
        var modules = GetMemoryModules();
        return new HardwareDetails
        {
            Cpu = GetCpu(),
            Gpu = GetGpu(),
            Displays = DisplayInfoService.Collect(),
            Memory = GetMemorySummary(modules),
            MemoryModules = modules,
            Motherboard = GetMotherboard(),
        };
    }

    // ---------------------------------------------------------------- CPU

    private static IReadOnlyList<InfoSection> GetCpu()
    {
        var sections = new List<InfoSection>();
        foreach (var p in Query("SELECT * FROM Win32_Processor"))
        {
            var maxMhz = ToULong(p["MaxClockSpeed"]);
            sections.Add(new InfoSection(Str(p["Name"]), [
                new("メーカー", CpuVendor(Str(p["Manufacturer"]))),
                new("モデル名称", Str(p["Name"])),
                new("コア数", Str(p["NumberOfCores"])),
                new("スレッド数", Str(p["NumberOfLogicalProcessors"])),
                new("基本クロック", maxMhz > 0 ? $"{maxMhz / 1000d:0.00} GHz ({maxMhz} MHz)" : NotAvailable),
                new("L2 キャッシュ", KbToText(ToULong(p["L2CacheSize"]))),
                new("L3 キャッシュ", KbToText(ToULong(p["L3CacheSize"]))),
                new("ソケット", Str(p["SocketDesignation"])),
                new("アーキテクチャ", CpuArchitecture(ToULong(p["Architecture"]))),
                new("仮想化支援", ToBool(p["VirtualizationFirmwareEnabled"]) ? "有効" : "無効"),
                new("プロセッサ ID", Str(p["ProcessorId"])),
            ]));
        }
        return sections;
    }

    private static string CpuVendor(string raw) => raw switch
    {
        "GenuineIntel" => "Intel",
        "AuthenticAMD" => "AMD",
        _ => raw,
    };

    private static string CpuArchitecture(ulong code) => code switch
    {
        0 => "x86",
        5 => "ARM",
        9 => "x64",
        12 => "ARM64",
        _ => NotAvailable,
    };

    // ---------------------------------------------------------------- GPU

    private static IReadOnlyList<InfoSection> GetGpu()
    {
        var vramByName = ReadVramFromRegistry();
        var sections = new List<InfoSection>();

        // Skip virtual display adapters (remote desktop tools etc.) when real PCI GPUs are present.
        var adapters = Query("SELECT * FROM Win32_VideoController");
        var physical = adapters.Where(g => Str(g["PNPDeviceID"]).StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)).ToList();
        if (physical.Count > 0) adapters = physical;

        foreach (var g in adapters)
        {
            var name = Str(g["Name"]);
            // Win32_VideoController.AdapterRAM is a UInt32 and caps at 4 GB, so prefer the registry value.
            ulong vram = vramByName.TryGetValue(name, out var reg) ? reg : ToULong(g["AdapterRAM"]);

            sections.Add(new InfoSection(name, [
                new("メーカー", GpuVendor(Str(g["AdapterCompatibility"]), name)),
                new("モデル名称", name),
                new("VRAM 容量", vram > 0 ? BytesToText(vram) : NotAvailable),
                new("GPU プロセッサ", Str(g["VideoProcessor"])),
                new("ドライバー バージョン", Str(g["DriverVersion"])),
                new("ドライバー日付", WmiDate(g["DriverDate"])),
                new("状態", Str(g["Status"])),
            ]));
        }
        return sections;
    }

    private static string GpuVendor(string compat, string name)
    {
        var s = (compat + " " + name).ToUpperInvariant();
        if (s.Contains("NVIDIA")) return "NVIDIA";
        if (s.Contains("AMD") || s.Contains("ADVANCED MICRO") || s.Contains("RADEON")) return "AMD";
        if (s.Contains("INTEL")) return "Intel";
        return string.IsNullOrWhiteSpace(compat) ? NotAvailable : compat;
    }

    private static Dictionary<string, ulong> ReadVramFromRegistry()
    {
        var result = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        const string classKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(classKey);
            if (root is null) return result;

            foreach (var sub in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc) continue;

                ulong size = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => (ulong)l,
                    byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0UL,
                    },
                };
                if (size > 0 && (!result.TryGetValue(desc, out var cur) || size > cur)) result[desc] = size;
            }
        }
        catch
        {
            // Registry access failures fall back to WMI's AdapterRAM.
        }
        return result;
    }

    // ---------------------------------------------------------------- Memory

    private static IReadOnlyList<MemoryModule> GetMemoryModules()
    {
        var list = new List<MemoryModule>();
        foreach (var m in Query("SELECT * FROM Win32_PhysicalMemory"))
        {
            var speed = ToULong(m["ConfiguredClockSpeed"]);
            if (speed == 0) speed = ToULong(m["Speed"]);
            var slot = Str(m["DeviceLocator"]);
            var bank = Str(m["BankLabel"]);

            list.Add(new MemoryModule(
                Slot: bank != NotAvailable && bank != slot ? $"{slot} ({bank})" : slot,
                Manufacturer: MemoryVendor(Str(m["Manufacturer"])),
                PartNumber: Str(m["PartNumber"]),
                Capacity: BytesToText(ToULong(m["Capacity"])),
                Type: MemoryType(ToULong(m["SMBIOSMemoryType"]), ToULong(m["MemoryType"])),
                Speed: speed > 0 ? $"{speed} MT/s" : NotAvailable,
                FormFactor: FormFactor(ToULong(m["FormFactor"]))));
        }
        return list;
    }

    private static IReadOnlyList<InfoSection> GetMemorySummary(IReadOnlyList<MemoryModule> modules)
    {
        ulong total = 0;
        foreach (var m in Query("SELECT Capacity FROM Win32_PhysicalMemory")) total += ToULong(m["Capacity"]);

        ulong slots = 0, maxCapacityKb = 0;
        foreach (var a in Query("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity FROM Win32_PhysicalMemoryArray"))
        {
            slots += ToULong(a["MemoryDevices"]);
            var max = ToULong(a["MaxCapacityEx"]);
            maxCapacityKb += max > 0 ? max : ToULong(a["MaxCapacity"]);
        }

        string Join(Func<MemoryModule, string> sel) =>
            modules.Count == 0 ? NotAvailable : string.Join(" / ", modules.Select(sel).Where(v => v != NotAvailable).Distinct().DefaultIfEmpty(NotAvailable));

        return
        [
            new InfoSection("メモリ概要", [
                new("メーカー", Join(m => m.Manufacturer)),
                new("規格 / タイプ", Join(m => m.Type)),
                new("総容量", total > 0 ? BytesToText(total) : NotAvailable),
                new("モジュール枚数", modules.Count.ToString(CultureInfo.InvariantCulture)),
                new("スロット数", slots > 0 ? $"{slots} (使用中 {modules.Count})" : NotAvailable),
                new("動作速度", Join(m => m.Speed)),
                new("形状", Join(m => m.FormFactor)),
                new("最大搭載容量", maxCapacityKb > 0 ? BytesToText(maxCapacityKb * 1024UL) : NotAvailable),
            ]),
        ];
    }

    private static string MemoryType(ulong smbios, ulong legacy) => (smbios != 0 ? smbios : legacy) switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => NotAvailable,
    };

    private static string FormFactor(ulong code) => code switch
    {
        8 => "DIMM",
        12 => "SO-DIMM",
        13 => "SRIMM",
        _ => NotAvailable,
    };

    // WMI sometimes reports the raw JEDEC manufacturer ID instead of a name.
    private static readonly Dictionary<string, string> JedecVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["80CE"] = "Samsung", ["CE00"] = "Samsung",
        ["80AD"] = "SK hynix", ["AD00"] = "SK hynix",
        ["802C"] = "Micron", ["2C00"] = "Micron",
        ["859B"] = "Crucial", ["9B85"] = "Crucial",
        ["0198"] = "Kingston", ["9801"] = "Kingston",
        ["029E"] = "Corsair", ["9E02"] = "Corsair",
        ["04CD"] = "G.Skill", ["CD04"] = "G.Skill",
        ["04CB"] = "ADATA", ["CB04"] = "ADATA",
        ["0B4E"] = "Team Group", ["4E0B"] = "Team Group",
        ["0194"] = "Patriot", ["8551"] = "Qimonda",
        ["80B3"] = "Nanya", ["830B"] = "Nanya",
    };

    private static string MemoryVendor(string raw)
    {
        if (raw == NotAvailable) return raw;
        var key = raw.Replace(" ", string.Empty);
        if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) key = key[2..];
        if (key.Length > 4) key = key[..4];
        return JedecVendors.TryGetValue(key, out var name) ? name : raw;
    }

    // ---------------------------------------------------------------- Motherboard

    private static IReadOnlyList<InfoSection> GetMotherboard()
    {
        var items = new List<InfoItem>();
        foreach (var b in Query("SELECT * FROM Win32_BaseBoard"))
        {
            items.Add(new("メーカー", Str(b["Manufacturer"])));
            items.Add(new("モデル名称", Str(b["Product"])));
            items.Add(new("リビジョン", Str(b["Version"])));
            items.Add(new("シリアル番号", Str(b["SerialNumber"])));
            break;
        }

        var bios = new List<InfoItem>();
        foreach (var b in Query("SELECT * FROM Win32_BIOS"))
        {
            bios.Add(new("BIOS メーカー", Str(b["Manufacturer"])));
            bios.Add(new("BIOS バージョン", Str(b["SMBIOSBIOSVersion"])));
            bios.Add(new("BIOS 日付", WmiDate(b["ReleaseDate"])));
            bios.Add(new("SMBIOS バージョン", $"{Str(b["SMBIOSMajorVersion"])}.{Str(b["SMBIOSMinorVersion"])}"));
            break;
        }

        var system = new List<InfoItem>();
        foreach (var c in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
        {
            system.Add(new("システム メーカー", Str(c["Manufacturer"])));
            system.Add(new("システム モデル", Str(c["Model"])));
            break;
        }
        foreach (var o in Query("SELECT Caption, Version, BuildNumber, OSArchitecture FROM Win32_OperatingSystem"))
        {
            system.Add(new("OS", Str(o["Caption"])));
            system.Add(new("OS バージョン", $"{Str(o["Version"])} (Build {Str(o["BuildNumber"])})"));
            system.Add(new("OS アーキテクチャ", Str(o["OSArchitecture"])));
            break;
        }

        return
        [
            new InfoSection("マザーボード", items),
            new InfoSection("BIOS / UEFI", bios),
            new InfoSection("システム", system),
        ];
    }

    // ---------------------------------------------------------------- Helpers

    private static List<ManagementBaseObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2", wql);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string Str(object? value)
    {
        var s = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(s) ? NotAvailable : s;
    }

    private static ulong ToULong(object? value)
    {
        try { return value is null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static bool ToBool(object? value) => value is bool b && b;

    private static string WmiDate(object? value)
    {
        if (value is not string s || s.Length < 8) return NotAvailable;
        try { return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy/MM/dd", CultureInfo.InvariantCulture); }
        catch { return NotAvailable; }
    }

    private static string KbToText(ulong kb) => kb == 0 ? NotAvailable : kb >= 1024 ? $"{kb / 1024d:0.#} MB" : $"{kb} KB";

    private static string BytesToText(ulong bytes)
    {
        const double gb = 1024d * 1024d * 1024d;
        return bytes >= 1024UL * 1024 * 1024
            ? $"{bytes / gb:0.##} GB"
            : $"{bytes / (1024d * 1024d):0} MB";
    }
}
