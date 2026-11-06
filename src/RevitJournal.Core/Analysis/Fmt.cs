using System.Globalization;

namespace RevitJournal.Core.Analysis;

/// <summary>Formatting for report text. Reports are English-only and culture-invariant.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Time(DateTime? t) => t?.ToString("HH:mm:ss", C) ?? "unknown time";

    public static string Stamp(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm", C) ?? "unknown";

    /// <summary>"today at 11:46", "yesterday at 09:12", "on Tue 03 Nov at 14:05".</summary>
    public static string When(DateTime t, DateTime now)
    {
        string time = t.ToString("HH:mm", C);
        return t.Date == now.Date ? $"today at {time}"
            : t.Date == now.Date.AddDays(-1) ? $"yesterday at {time}"
            : $"on {t.ToString("ddd dd MMM", C)} at {time}";
    }

    public static string Ago(TimeSpan d) => d.TotalMinutes < 1 ? "just now"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
        : $"{(int)d.TotalHours} h {d.Minutes} min ago";

    public static string Mb(int mb) => mb >= 1024 ? (mb / 1024.0).ToString("0.0", C) + " GB" : mb.ToString(C) + " MB";

    public static string Duration(TimeSpan d) => d.TotalSeconds < 60
        ? $"{d.TotalSeconds:0} s"
        : d.TotalHours < 1
            ? $"{(int)d.TotalMinutes} min {d.Seconds} s"
            : $"{(int)d.TotalHours} h {d.Minutes} min";

    /// <summary>Model name from a local, server or cloud path ("Autodesk Docs://Project/Model.rvt").</summary>
    public static string FileName(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/', '}']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    public static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
