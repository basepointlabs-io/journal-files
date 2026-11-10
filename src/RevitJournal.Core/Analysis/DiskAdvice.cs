using RevitJournal.Core.Live;
using RevitJournal.Core.Model;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Analysis;

/// <summary>How full the disk was when a session ended, judged against what Autodesk requires.</summary>
public static class DiskAdvice
{
    public const string AlmostFullRule = "metric-disk-almost-full";
    public const string BelowMinimumRule = "metric-disk-below-autodesk-minimum";

    /// <summary>Autodesk: "Minimum 30 GB free space is required in the hard drive."</summary>
    public const long AutodeskMinimumMb = 30 * 1024;
    /// <summary>Below this the disk itself is the problem to solve first.</summary>
    public const long AlmostFullMb = HealthMonitor.DiskDangerMb;

    /// <summary>A reading taken at analysis time still describes a session that ended at most this long ago.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(1);

    /// <summary>
    /// The reading that describes the end of the session: the recorded one when the tray monitor was running, else the
    /// current one if the session has only just ended. Null for journals from another computer and for old sessions
    /// without a recorded reading.
    /// </summary>
    public static DiskSpace? AtEnd(JournalSession s, AnalysisContext ctx, DiskHistory? history)
    {
        if (!ctx.IsLocal || s.LastTimestamp is not { } end)
            return null;
        if (history is not null && !ctx.IsBeingWritten && Drive(s.FilePath) is { } drive && history.At(drive, end) is { } recorded)
            return recorded;
        return ctx.Disk is { } now && (ctx.IsBeingWritten || (now.At >= end - DiskHistory.Before && now.At - end <= FreshFor)) ? now : null;
    }

    public static List<string> Facts(JournalSession s, DiskSpace d)
    {
        string when = s.LastTimestamp is { } end && (d.At - end).Duration() >= TimeSpan.FromMinutes(1)
            ? $"{Fmt.Stamp(d.At)}, {Fmt.Duration((d.At - end).Duration())} {(d.At < end ? "before" : "after")} the journal ends"
            : Fmt.Stamp(d.At);
        return
        [
            $"Drive {d.Name} had {Fmt.Mb((int)Math.Min(d.FreeMb, int.MaxValue))} free at {when}. Autodesk requires at least 30 GB of free disk space for Revit.",
        ];
    }

    public static Severity Severity(string ruleId, SessionStatus status) => ruleId == AlmostFullRule
        ? status is SessionStatus.EndedAbnormally or SessionStatus.ShutdownIncomplete ? Rules.Severity.Critical : Rules.Severity.High
        : status == SessionStatus.EndedAbnormally ? Rules.Severity.Medium : Rules.Severity.Low;

    public static List<string> Steps(JournalSession s, AnalysisContext ctx)
    {
        string drive = ctx.Disk?.Name ?? "C:";
        string year = Metrics.RunningYear(s)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "20xx";
        return
        [
            AutodeskArticle.ClosedUnexpectedly.Cite($"Free space on drive {drive} until at least 30 GB are free."),
            AutodeskArticle.DiskFull.Cite("Work with your IT support to free up space, and run Windows Disk Cleanup."),
            AutodeskArticle.DiskFull.Cite($"With Revit closed, delete the files in the cloud model caches: C:\\Users\\%USERNAME%\\AppData\\Local\\Autodesk\\Revit\\Autodesk Revit {year}\\CollaborationCache and C:\\Users\\%USERNAME%\\AppData\\Local\\Autodesk\\Revit\\PacCache (the Logs folder cannot be deleted). Cloud models open slowly the first time afterwards."),
            AutodeskArticle.DiskFull.Cite("Delete the copies of models that Revit keeps in the journal folder."),
            AutodeskArticle.DiskFull.Cite("If the computer has a second drive, move the Windows Temp folder to it."),
        ];
    }

    private static string? Drive(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? root.TrimEnd('\\', '/') : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
