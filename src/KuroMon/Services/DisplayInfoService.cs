using System.Globalization;
using System.Runtime.InteropServices;
using KuroMon.Models;

namespace KuroMon.Services;

/// <summary>
/// Enumerates every active monitor through the Windows DisplayConfig API.
/// (Win32_VideoController only reports one mode per adapter, so it cannot describe multi-monitor setups.)
/// </summary>
public static class DisplayInfoService
{
    public static IReadOnlyList<InfoSection> Collect()
    {
        var sections = new List<InfoSection>();
        try
        {
            var adapters = ReadAdapterNames();
            int index = 0;

            foreach (var d in QueryActivePaths().OrderByDescending(d => d.IsPrimary).ThenBy(d => d.X).ThenBy(d => d.Y))
            {
                index++;
                var title = $"ディスプレイ {index}: {d.MonitorName}" + (d.IsPrimary ? "（メイン）" : string.Empty);
                var maxHz = MaxRefreshRate(d.GdiName, d.Width, d.Height);

                sections.Add(new InfoSection(title, [
                    new("モニター名", d.MonitorName),
                    new("解像度", $"{d.Width} x {d.Height}"),
                    new("リフレッシュレート", $"{d.RefreshHz.ToString("0.##", CultureInfo.InvariantCulture)} Hz"),
                    new("最大リフレッシュレート", maxHz > 0 ? $"{maxHz} Hz（この解像度で選択可能な最大値）" : "N/A"),
                    new("接続方式", Connector(d.OutputTechnology)),
                    new("メインディスプレイ", d.IsPrimary ? "はい" : "いいえ"),
                    new("位置", $"({d.X}, {d.Y})"),
                    new("回転", Rotation(d.Rotation)),
                    new("接続先 GPU", adapters.TryGetValue(d.GdiName, out var gpu) ? gpu : "N/A"),
                    new("Windows デバイス名", d.GdiName),
                ]));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Display enumeration failed.", ex);
        }

        if (sections.Count == 0)
        {
            sections.Add(new InfoSection("ディスプレイ", [new("状態", "ディスプレイ情報を取得できませんでした")]));
        }
        return sections;
    }

    private sealed record DisplayPath(
        string GdiName, string MonitorName, int Width, int Height, int X, int Y,
        double RefreshHz, uint OutputTechnology, uint Rotation)
    {
        public bool IsPrimary => X == 0 && Y == 0;
    }

    private static List<DisplayPath> QueryActivePaths()
    {
        var result = new List<DisplayPath>();
        DisplayConfigPathInfo[] paths;
        DisplayConfigModeInfo[] modes;
        int err;

        do
        {
            err = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (err != 0) return result;

            paths = new DisplayConfigPathInfo[pathCount];
            modes = new DisplayConfigModeInfo[modeCount];
            err = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err == 0)
            {
                Array.Resize(ref paths, (int)pathCount);
                Array.Resize(ref modes, (int)modeCount);
            }
        }
        while (err == ErrorInsufficientBuffer);

        if (err != 0) return result;

        foreach (var p in paths)
        {
            var source = new DisplayConfigSourceDeviceName();
            source.Header.Type = GetSourceName;
            source.Header.Size = (uint)Marshal.SizeOf<DisplayConfigSourceDeviceName>();
            source.Header.AdapterId = p.SourceInfo.AdapterId;
            source.Header.Id = p.SourceInfo.Id;
            DisplayConfigGetDeviceInfo(ref source);

            var target = new DisplayConfigTargetDeviceName();
            target.Header.Type = GetTargetName;
            target.Header.Size = (uint)Marshal.SizeOf<DisplayConfigTargetDeviceName>();
            target.Header.AdapterId = p.TargetInfo.AdapterId;
            target.Header.Id = p.TargetInfo.Id;
            DisplayConfigGetDeviceInfo(ref target);

            int width = 0, height = 0, x = 0, y = 0;
            if (p.SourceInfo.ModeInfoIdx < modes.Length && modes[p.SourceInfo.ModeInfoIdx].InfoType == ModeInfoTypeSource)
            {
                var sm = modes[p.SourceInfo.ModeInfoIdx].SourceMode;
                width = (int)sm.Width;
                height = (int)sm.Height;
                x = sm.PositionX;
                y = sm.PositionY;
            }

            double hz = p.TargetInfo.RefreshRate.Denominator == 0
                ? 0
                : (double)p.TargetInfo.RefreshRate.Numerator / p.TargetInfo.RefreshRate.Denominator;

            var name = string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName)
                ? (IsVirtual(target.OutputTechnology) ? "仮想ディスプレイ" : "不明なモニター")
                : target.MonitorFriendlyDeviceName;

            result.Add(new DisplayPath(source.ViewGdiDeviceName ?? string.Empty, name, width, height, x, y,
                hz, target.OutputTechnology, p.TargetInfo.Rotation));
        }
        return result;
    }

    /// <summary>Maps "\\.\DISPLAYn" to the GPU name that drives it.</summary>
    private static Dictionary<string, string> ReadAdapterNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            if (!string.IsNullOrEmpty(dd.DeviceName)) map[dd.DeviceName] = dd.DeviceString;
            dd = new DisplayDevice { Cb = Marshal.SizeOf<DisplayDevice>() };
        }
        return map;
    }

    private static int MaxRefreshRate(string gdiName, int width, int height)
    {
        if (string.IsNullOrEmpty(gdiName)) return 0;
        int max = 0;
        var mode = new DevMode { dmSize = (ushort)Marshal.SizeOf<DevMode>() };
        for (int i = 0; EnumDisplaySettings(gdiName, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == width && mode.dmPelsHeight == height) max = Math.Max(max, (int)mode.dmDisplayFrequency);
        }
        return max;
    }

    private static bool IsVirtual(uint tech) => tech is 15 or 16 or 17;

    private static string Connector(uint tech) => tech switch
    {
        0 => "VGA (D-Sub)",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS (内蔵)",
        10 => "DisplayPort",
        11 => "DisplayPort (内蔵)",
        12 => "UDI",
        13 => "UDI (内蔵)",
        15 => "Miracast (ワイヤレス)",
        16 => "間接接続 (USB 等)",
        17 => "仮想",
        0x80000000 => "内蔵パネル",
        _ => "その他",
    };

    private static string Rotation(uint r) => r switch
    {
        1 => "なし",
        2 => "90°",
        3 => "180°",
        4 => "270°",
        _ => "N/A",
    };

    // ------------------------------------------------------------------ Win32 interop

    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const uint ModeInfoTypeSource = 1;
    private const uint GetSourceName = 1;
    private const uint GetTargetName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigSourceMode
    {
        public uint Width;
        public uint Height;
        public uint PixelFormat;
        public int PositionX;
        public int PositionY;
    }

    // DISPLAYCONFIG_MODE_INFO is 64 bytes: header (16) + union (48). Only the source mode is read.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public uint Id;
        [FieldOffset(8)] public Luid AdapterId;
        [FieldOffset(16)] public DisplayConfigSourceMode SourceMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] DisplayConfigPathInfo[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
}
