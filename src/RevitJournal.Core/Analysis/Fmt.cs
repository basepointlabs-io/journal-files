using System.Globalization;

namespace RevitJournal.Core.Analysis;

/// <summary>Formatting for report text. Reports are English-only and culture-invariant.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Time(DateTime? t) => t?.ToString("HH:mm:ss", C) ?? "unknown time";

    public static string Stamp(DateTime? t) => t?.ToString("yyyy-MM-dd HH:mm", C) ?? "unknown";

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
