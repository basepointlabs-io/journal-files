using System.Globalization;
using RevitJournal.Core.Analysis;

namespace RevitJournal.Core.Live;

/// <summary>
/// Free disk space over time, written by the tray monitor to one small file in the user's profile. A report is
/// usually opened after the computer was restarted, when the disk has room again; this history tells how much was
/// free when the Revit session actually ended.
/// </summary>
public sealed class DiskHistory(string filePath)
{
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    /// <summary>A reading is stored when this much time has passed, or sooner when free space moved noticeably.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public const long NoticeableChangeMb = 512;
    /// <summary>How far from the end of a session a reading may be and still describe it.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(15), After = TimeSpan.FromMinutes(5);

    private static readonly Lazy<DiskHistory> Shared = new(() => new DiskHistory(Path.Combine(AppFolders.Data, "disk-history.csv")));

    /// <summary>The history of this computer, next to the app's settings.</summary>
    public static DiskHistory Default => Shared.Value;

    private readonly object _gate = new();
    private List<(DateTime Time, string Drive, long FreeMb)>? _samples;

    /// <summary>Stores the reading of each drive if it is due. Never throws: the history is a convenience.</summary>
    public void Record(DateTime time, IEnumerable<DiskSample> disks)
    {
        lock (_gate)
        {
            var samples = Load();
            var lines = new List<string>();
            foreach (var d in disks)
            {
                var last = samples.LastOrDefault(s => s.Drive.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                if (last.Drive is not null && time - last.Time < Interval && Math.Abs(d.FreeMb - last.FreeMb) < NoticeableChangeMb)
                    continue;
                samples.Add((time, d.Name, d.FreeMb));
                lines.Add(Line((time, d.Name, d.FreeMb)));
            }
            if (lines.Count == 0)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                if (samples.Count > 0 && time - samples[0].Time > Keep + TimeSpan.FromDays(1))
                {
                    samples.RemoveAll(s => time - s.Time > Keep);
                    File.WriteAllLines(filePath, samples.Select(Line));
                }
                else
                    File.AppendAllLines(filePath, lines);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk is exactly when writing fails; the readings stay in memory for this run.
            }
        }
    }

    /// <summary>Free space of <paramref name="drive"/> ("C:") closest to <paramref name="moment"/>, or null when nothing was recorded around it.</summary>
    public DiskSpace? At(string drive, DateTime moment)
    {
        lock (_gate)
        {
            (DateTime Time, string Drive, long FreeMb)? best = null;
            foreach (var s in Load())
            {
                if (!s.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase) || s.Time < moment - Before || s.Time > moment + After)
                    continue;
                if (best is null || (s.Time - moment).Duration() < (best.Value.Time - moment).Duration())
                    best = s;
            }
            return best is { } b ? new DiskSpace(b.Drive, b.FreeMb, b.Time) : null;
        }
    }

    private List<(DateTime Time, string Drive, long FreeMb)> Load()
    {
        if (_samples is not null)
            return _samples;
        _samples = [];
        try
        {
            if (File.Exists(filePath))
            {
                foreach (string line in File.ReadLines(filePath))
                {
                    string[] p = line.Split(';');
                    if (p.Length == 3 && DateTime.TryParseExact(p[0], "s", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                        && long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long free))
                        _samples.Add((t, p[1], free));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return _samples;
    }

    private static string Line((DateTime Time, string Drive, long FreeMb) s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Time:s};{s.Drive};{s.FreeMb}");
}
