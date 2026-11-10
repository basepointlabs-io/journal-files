using System.Text;

namespace RevitJournal.Core.Parsing;

/// <summary>Reads journal files: ANSI (Windows-1252) by default, UTF-8/UTF-16 when a BOM is present.</summary>
public static class JournalReader
{
    static JournalReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string[] ReadLines(string path) => [.. EnumerateLines(path)];

    /// <summary>Reads line by line without holding the whole journal in memory.</summary>
    public static IEnumerable<string> EnumerateLines(string path)
    {
        // Revit keeps the journal of a running session open for writing, so allow shared access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    /// <summary>The complete lines within the last <paramref name="maxBytes"/> of the file: how a journal ends, without reading it all.</summary>
    public static List<string> ReadTail(string path, int maxBytes = 1 << 14)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool cut = stream.Length > maxBytes;
        if (cut)
            stream.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: !cut);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        if (cut && lines.Count > 0)
            lines.RemoveAt(0); // starts in the middle of a line
        return lines;
    }

    /// <summary>
    /// True when another process (Revit) still has the file open for writing — i.e. the session is running
    /// or Revit is hung. Opening with FileShare.Read fails if any writer holds the file.
    /// </summary>
    public static bool IsBeingWritten(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
