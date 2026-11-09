using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>What Windows recorded about Revit.exe: a crash (Application Error) or a hang that was closed (Application Hang).</summary>
/// <param name="ProcessStart">When the Revit process that crashed was started; ties the event to one journal exactly.</param>
/// <param name="RevitYear">"2025" from "...\Revit 2025\Revit.exe".</param>
public sealed record WindowsCrashEvent(DateTime Time, bool IsHang, string? Module, string? ExceptionCode,
    DateTime? ProcessStart = null, string? RevitYear = null);

/// <summary>How a session without shutdown lines really ended, as far as it can be proven.</summary>
public enum EndKind
{
    /// <summary>Not an abnormal end.</summary>
    None,
    /// <summary>No shutdown lines and no proof of a crash: Revit was probably closed forcibly, or the file is a copy of a running session.</summary>
    Unconfirmed,
    /// <summary>Windows recorded that Revit stopped responding and was closed.</summary>
    Hang,
    /// <summary>A crash dump, a crash record in the journal or a Windows crash event exists.</summary>
    Crash,
}

/// <summary>
/// Reads Revit.exe crash and hang events from the Windows Application event log of this computer. Nothing is written
/// and nothing leaves the machine; ordinary users may read this log.
/// </summary>
public static class WindowsCrashLog
{
    private static readonly object Gate = new();
    private static IReadOnlyList<WindowsCrashEvent> _cache = [];
    private static DateTime _loadedAt;

    /// <summary>Events of the last 30 days, cached for half a minute (the list is consulted once per journal).</summary>
    public static IReadOnlyList<WindowsCrashEvent> Recent(bool fresh = false)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        lock (Gate)
        {
            if (fresh || DateTime.UtcNow - _loadedAt > TimeSpan.FromSeconds(30))
            {
                _cache = Load();
                _loadedAt = DateTime.UtcNow;
            }
            return _cache;
        }
    }

    /// <summary>Windows writes the FILETIME as decimal digits on current versions and as hex ("01dc...") on older ones.</summary>
    internal static DateTime? ParseFileTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        bool hex = text.StartsWith('0') || text.Any(char.IsAsciiLetter);
        long ticks;
        if (hex ? !long.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out ticks) : !long.TryParse(text, out ticks))
            return null;
        // Plausible range only (years 2000-2100), so a different field layout cannot produce nonsense.
        return ticks is > 125911584000000000 and < 157469184000000000 ? DateTime.FromFileTime(ticks) : null;
    }

    internal static string? YearFromPath(string? path)
    {
        const string marker = "\\Revit ";
        int at = path?.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0 || path!.Length < at + marker.Length + 4)
            return null;
        string year = path.Substring(at + marker.Length, 4);
        return year.All(char.IsAsciiDigit) ? year : null;
    }

    [SupportedOSPlatform("windows")]
    private static List<WindowsCrashEvent> Load()
    {
        var result = new List<WindowsCrashEvent>();
        try
        {
            // 1000 = Application Error (crash), 1002 = Application Hang; timediff is in milliseconds (30 days).
            const string query = "*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]";
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, query));
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    var p = e.Properties;
                    if (p.Count == 0 || e.TimeCreated is not { } time
                        || !string.Equals(p[0].Value as string, "Revit.exe", StringComparison.OrdinalIgnoreCase))
                        continue;
                    bool hang = e.Id == 1002;
                    // Application Error: 0 app, 3 faulting module, 6 exception code, 9 process start (FILETIME), 10 app path.
                    // Application Hang:  0 app, 3 process start (FILETIME), 5 app path.
                    string? Text(int i) => i < p.Count ? p[i].Value?.ToString() : null;
                    result.Add(new WindowsCrashEvent(time, hang,
                        hang ? null : Text(3), hang ? null : Text(6),
                        ParseFileTime(Text(hang ? 3 : 9)), YearFromPath(Text(hang ? 5 : 10))));
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The log is optional evidence; without it the journal and dump files still decide.
        }
        return result;
    }
}

public static class CrashEvidence
{
    /// <summary>The journal's first line is written within seconds of the Revit process starting.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The Windows event of this session: matched by the process start time when the event carries one (exact even
    /// when Revit hung for a long time before it died, or several Revit windows were open), else by time of death.
    /// </summary>
    internal static WindowsCrashEvent? EventFor(JournalSession s, IReadOnlyList<WindowsCrashEvent> events)
    {
        string? year = s.Release is { Length: >= 4 } r ? r[..4] : null;
        foreach (var e in events)
        {
            if (e.RevitYear is not null && year is not null && e.RevitYear != year)
                continue;
            if (e.ProcessStart is { } started)
            {
                if (s.Start is { } start && (started - start).Duration() <= StartTolerance)
                    return e;
            }
            else if (s.LastTimestamp is { } last && e.Time >= last - Before && e.Time <= last + After)
                return e;
        }
        return null;
    }

    /// <summary>A Windows event belongs to a session when it falls between the journal's last line and half an hour later.</summary>
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan After = TimeSpan.FromMinutes(30);

    /// <summary>Evidence for how an abnormally ended session ended, strongest first, with the resulting classification.</summary>
    public static (EndKind Kind, List<string> Facts) Find(JournalSession s, AnalysisContext context)
    {
        var facts = new List<string>();
        var kind = EndKind.Unconfirmed;

        foreach (string dump in context.DumpFiles)
        {
            facts.Add($"Revit wrote a crash dump next to the journal: {Path.GetFileName(dump)}.");
            kind = EndKind.Crash;
        }

        // Crash records Revit writes itself, only near the end: earlier ones were survived.
        var marker = s.FatalMarkers.LastOrDefault(m => m.Line > s.LineCount - 400);
        if (marker is not null)
        {
            facts.Add($"The journal records the crash itself (line {marker.Line}): {Fmt.Trim(marker.Text, 140)}");
            kind = EndKind.Crash;
        }

        {
            var e = EventFor(s, context.CrashEvents);
            if (e is { IsHang: false })
            {
                facts.Add($"Windows recorded a crash of Revit.exe at {Fmt.Time(e.Time)}"
                          + (e.Module is { Length: > 0 } m ? $", in module {m}" : "")
                          + (e.ExceptionCode is { Length: > 0 } c ? $" (exception code {c}{CodeName(c)})" : "") + "."
                          + ModuleHint(e.Module));
                kind = EndKind.Crash;
            }
            else if (e is { IsHang: true })
            {
                facts.Add($"Windows recorded that Revit stopped responding and was closed at {Fmt.Time(e.Time)}.");
                if (kind != EndKind.Crash)
                    kind = EndKind.Hang;
            }
        }

        if (kind == EndKind.Unconfirmed)
        {
            facts.Add(context.IsLocal
                ? "No crash dump, crash record or Windows crash event was found for this session. Most likely Revit was closed forcibly (Task Manager, Windows shutdown, power loss)."
                : "This file is not in this computer's Revit journal folder and contains no crash record. If it was copied while Revit was still running, the session simply continued after the copy.");
        }
        return (kind, facts);
    }

    /// <summary>The common Windows exception codes in words.</summary>
    private static string CodeName(string code) => code.TrimStart('0', 'x').ToLowerInvariant() switch
    {
        "c0000005" => ", access violation",
        "c00000fd" => ", stack overflow",
        "c0000409" => ", stack buffer overrun / fail-fast",
        "e0434352" => ", unhandled .NET exception",
        "c000001d" => ", illegal instruction",
        _ => "",
    };

    /// <summary>What a faulting module usually means, when that is well known.</summary>
    private static string ModuleHint(string? module)
    {
        if (string.IsNullOrEmpty(module))
            return "";
        string m = module.ToLowerInvariant();
        if (m.StartsWith("nv", StringComparison.Ordinal) || m.StartsWith("ati", StringComparison.Ordinal) || m.StartsWith("amd", StringComparison.Ordinal)
            || m.StartsWith("ig", StringComparison.Ordinal) || m.StartsWith("d3d", StringComparison.Ordinal) || m.StartsWith("dxgi", StringComparison.Ordinal))
            return " That is a graphics component: update the graphics driver, or turn off Options > Hardware > Use hardware acceleration to test.";
        if (m is "coreclr.dll" or "clr.dll" or "clrjit.dll" or "hostfxr.dll" or "hostpolicy.dll")
            return " That is the .NET runtime, which runs add-ins and parts of Revit: test with third-party add-ins disabled.";
        if (m is "ntdll.dll" or "kernelbase.dll" or "ucrtbase.dll" or "revit.exe")
            return "";
        if (!m.Contains("revit", StringComparison.Ordinal) && !m.Contains("autodesk", StringComparison.Ordinal) && !m.StartsWith("ads", StringComparison.Ordinal))
            return " If this file belongs to an add-in, disable that add-in and test again.";
        return "";
    }
}
