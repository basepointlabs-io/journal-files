using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RevitJournal.Core.Live;

/// <summary>One Revit.exe process as Windows sees it right now.</summary>
/// <param name="MemoryMb">Private memory: what this Revit alone has taken from the machine.</param>
/// <param name="CpuSeconds">Total processor time used so far; the monitor turns two readings into a percentage.</param>
/// <param name="GdiObjects">Drawing resources in use; Windows gives a process at most 10,000.</param>
/// <param name="UserObjects">Window resources in use; same limit.</param>
public sealed record RevitProcessSample(int Id, string Version, DateTime StartTime, bool Responding, long MemoryMb,
    double CpuSeconds, int GdiObjects, int UserObjects);

public sealed record DiskSample(string Name, long FreeMb, long TotalMb);

/// <summary>A reading of the computer's resources. Nothing here comes from the journal: it is the state at this second.</summary>
/// <param name="CommitLimitMb">RAM plus page file: all the memory Windows can promise to programs.</param>
/// <param name="CpuIdleSeconds">Idle and total processor time since boot, summed over all cores.</param>
public sealed record SystemSample(DateTime Time, long TotalRamMb, long FreeRamMb, long CommitLimitMb, long CommitFreeMb,
    double CpuIdleSeconds, double CpuTotalSeconds, int ProcessorCount,
    IReadOnlyList<RevitProcessSample> Revits, IReadOnlyList<DiskSample> Disks);

public interface ISystemProbe
{
    SystemSample Sample();
}

/// <summary>Reads memory, processor, disk and per-Revit resources from Windows. Local calls only.</summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    public SystemSample Sample()
    {
        if (!OperatingSystem.IsWindows())
            return new SystemSample(DateTime.Now, 0, 0, 0, 0, 0, 0, Environment.ProcessorCount, [], []);

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        double idle = 0, total = 0;
        if (GetSystemTimes(out long idleTime, out long kernelTime, out long userTime))
        {
            // Kernel time includes idle time.
            idle = idleTime / 1e7;
            total = (kernelTime + userTime) / 1e7;
        }
        return new SystemSample(DateTime.Now,
            Mb(mem.ullTotalPhys), Mb(mem.ullAvailPhys), Mb(mem.ullTotalPageFile), Mb(mem.ullAvailPageFile),
            idle, total, Environment.ProcessorCount, Revits(), Disks());
    }

    [SupportedOSPlatform("windows")]
    private static List<RevitProcessSample> Revits()
    {
        var result = new List<RevitProcessSample>();
        foreach (var p in Process.GetProcessesByName("Revit"))
        {
            try
            {
                // ...\Autodesk\Revit 2025\Revit.exe -> "2025"
                string dir = Path.GetFileName(Path.GetDirectoryName(p.MainModule?.FileName) ?? "");
                string version = dir.StartsWith("Revit ", StringComparison.OrdinalIgnoreCase) ? dir[6..] : "";
                result.Add(new RevitProcessSample(p.Id, version, p.StartTime, p.Responding, p.PrivateMemorySize64 / (1024 * 1024),
                    p.TotalProcessorTime.TotalSeconds, (int)GetGuiResources(p.Handle, 0), (int)GetGuiResources(p.Handle, 1)));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // The process exited or belongs to another user.
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    /// <summary>The drives Revit depends on: Windows, temporary files, and the user profile with the cloud model cache.</summary>
    private static List<DiskSample> Disks()
    {
        var result = new List<DiskSample>();
        string?[] places =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (string root in places.Select(p => Path.GetPathRoot(p)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                    result.Add(new DiskSample(root.TrimEnd('\\'), drive.AvailableFreeSpace / (1024 * 1024), drive.TotalSize / (1024 * 1024)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return result;
    }

    private static long Mb(ulong bytes) => (long)(bytes / (1024 * 1024));

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>flags: 0 = GDI objects, 1 = USER objects.</summary>
    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);
}
