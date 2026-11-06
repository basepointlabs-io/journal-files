using System.Text;

namespace RevitJournal.Core.Parsing;

/// <summary>Reads journal files: ANSI (Windows-1252) by default, UTF-8/UTF-16 when a BOM is present.</summary>
public static class JournalReader
{
    static JournalReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string[] ReadLines(string path)
    {
        // Revit keeps the journal of a running session open for writing, so allow shared access.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.GetEncoding(1252), detectEncodingFromByteOrderMarks: true);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return [.. lines];
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
