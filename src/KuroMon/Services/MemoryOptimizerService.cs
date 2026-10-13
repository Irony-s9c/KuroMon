using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KuroMon.Services;

/// <param name="ProcessesTrimmed">Processes whose working set was emptied.</param>
/// <param name="ProcessesSkipped">Processes that were protected or could not be opened (e.g. anti-cheat / protected processes).</param>
/// <param name="WorkingSetReleasedBytes">Sum of working set reductions of the trimmed processes.</param>
/// <param name="StandbyPurged">True when the standby list (file cache) was purged.</param>
public sealed record MemoryOptimizeResult(
    DateTime Time,
    bool Automatic,
    int ProcessesTrimmed,
    int ProcessesSkipped,
    long WorkingSetReleasedBytes,
    bool StandbyPurged,
    ulong AvailableBefore,
    ulong AvailableAfter,
    string? ProtectedGame);

public sealed record MemoryOptimizeOptions(bool PurgeStandbyList, IReadOnlyCollection<string> ExtraExclusions, int? ProtectedProcessId);

/// <summary>
/// "Memory release" for gaming:
/// 1. Empties the working sets of background processes in the user's session (EmptyWorkingSet).
///    Their pages move to the standby / modified lists and come back on demand, so nothing is lost.
/// 2. Optionally purges the standby list (file cache) like ISLC, so the game gets free pages immediately.
/// The game itself, streaming software, the shell and system processes are never trimmed.
/// Requires administrator rights (KuroMon always runs elevated).
/// </summary>
public static class MemoryOptimizerService
{
    /// <summary>Never trimmed: trimming these causes stutter in the game, audio, capture or the desktop shell.</summary>
    public static readonly IReadOnlySet<string> BuiltInExclusions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows session / shell
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "dwm", "fontdrvhost",
        "explorer", "sihost", "ctfmon", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost",
        "audiodg", "MemCompression",
        // Streaming / capture (trimming them causes dropped frames while streaming)
        "obs64", "obs32", "obs", "Streamlabs OBS", "Streamlabs Desktop", "XSplit.Core", "nvcontainer", "NVIDIA Share",
        "NVIDIA Overlay", "GameBar", "GameBarFTServer", "AMDRSServ", "RadeonSoftware",
    };

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsRunning => Gate.CurrentCount == 0;

    public static async Task<MemoryOptimizeResult?> OptimizeAsync(MemoryOptimizeOptions options, bool automatic)
    {
        if (!await Gate.WaitAsync(0)) return null; // already running
        try
        {
            return await Task.Run(() => Optimize(options, automatic));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static MemoryOptimizeResult Optimize(MemoryOptimizeOptions options, bool automatic)
    {
        var before = AvailablePhysical();
        int self = Environment.ProcessId;
        int session;
        using (var me = Process.GetCurrentProcess()) session = me.SessionId;
        string? gameName = null;

        int trimmed = 0, skipped = 0;
        long released = 0;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.Id <= 4 || p.SessionId != session) continue;

                    if (p.Id == options.ProtectedProcessId)
                    {
                        gameName = p.ProcessName;
                        skipped++;
                        continue;
                    }
                    if (BuiltInExclusions.Contains(p.ProcessName) || options.ExtraExclusions.Contains(p.ProcessName))
                    {
                        skipped++;
                        continue;
                    }

                    var r = TrimProcess(p.Id);
                    if (r is null) skipped++;
                    else
                    {
                        trimmed++;
                        released += Math.Max(0, r.Value);
                    }
                }
                catch
                {
                    skipped++; // process exited or access denied
                }
            }
        }

        bool purged = options.PurgeStandbyList && PurgeStandbyList();
        var after = AvailablePhysical();

        var result = new MemoryOptimizeResult(DateTime.Now, automatic, trimmed, skipped, released, purged, before, after, gameName);
        AppLog.Info($"Memory optimize ({(automatic ? "auto" : "manual")}): trimmed={trimmed} skipped={skipped} " +
                    $"released={released / 1048576} MB standby={(purged ? "purged" : "kept")} " +
                    $"available={before / 1048576} -> {after / 1048576} MB game={gameName ?? "-"}");
        return result;
    }

    /// <summary>Empties one working set. Returns the reduction in bytes, or null when the process could not be opened.</summary>
    private static long? TrimProcess(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessSetQuota | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var counters = new ProcessMemoryCounters { cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
            ulong ws0 = K32GetProcessMemoryInfo(handle, ref counters, counters.cb) ? (ulong)counters.WorkingSetSize : 0;

            if (!EmptyWorkingSet(handle)) return null;

            ulong ws1 = K32GetProcessMemoryInfo(handle, ref counters, counters.cb) ? (ulong)counters.WorkingSetSize : ws0;
            return (long)ws0 - (long)ws1;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Purges the standby list (MemoryPurgeStandbyList). Needs SeProfileSingleProcessPrivilege.</summary>
    private static bool PurgeStandbyList()
    {
        try
        {
            if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            {
                AppLog.Warn("Standby purge skipped: SeProfileSingleProcessPrivilege could not be enabled.");
                return false;
            }

            int command = MemoryPurgeStandbyList;
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
            if (status != 0) AppLog.Warn($"Standby purge failed: NTSTATUS 0x{status:X8}");
            return status == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Standby purge failed.", ex);
            return false;
        }
    }

    public static ulong AvailablePhysical()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0;
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
            return Marshal.GetLastWin32Error() == 0; // ERROR_NOT_ALL_ASSIGNED (1300) when the token lacks it
        }
        finally
        {
            CloseHandle(token);
        }
    }

    // ------------------------------------------------------------------ Win32

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessVmRead = 0x0010;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EmptyWorkingSet(IntPtr process);

    private static bool EmptyWorkingSet(IntPtr process) => K32EmptyWorkingSet(process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
