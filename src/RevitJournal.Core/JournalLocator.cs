using System.Text.RegularExpressions;

namespace RevitJournal.Core;

/// <summary>A journal file on disk, without parsing it.</summary>
public sealed record JournalFile(string Path, string RevitVersion, int Number, DateTime LastWrite, long SizeBytes)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Finds Revit journals in %LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 20XX\Journals.</summary>
public static partial class JournalLocator
{
    /// <summary>
    /// "journal.0669.txt", or "journal.0669.txt.copy.txt": when Revit crashes, its error report (CER) leaves a copy of
    /// the journal under that name, and often the original is gone. Both are the journal of session 0669.
    /// </summary>
    [GeneratedRegex(@"^journal\.(\d+)\.txt(\.copy\.txt)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MainJournalName();

    /// <summary>"journal.0669" for "journal.0669.txt" and for its crash copy "journal.0669.txt.copy.txt": the name its
    /// crash dumps (journal.0669.0001.dmp) and worker logs (journal.0669.worker1.log) start with.</summary>
    public static string Stem(string journalPath)
    {
        string name = System.IO.Path.GetFileName(journalPath);
        var m = MainJournalName().Match(name);
        return m.Success ? $"journal.{m.Groups[1].Value}" : System.IO.Path.GetFileNameWithoutExtension(journalPath);
    }

    public static string DefaultRoot =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Autodesk", "Revit");

    /// <summary>All main journals (worker logs excluded), newest first.</summary>
    public static List<JournalFile> FindAll(string? root = null)
    {
        root ??= DefaultRoot;
        var result = new List<JournalFile>();
        if (!Directory.Exists(root))
            return result;

        foreach (string versionDir in Directory.EnumerateDirectories(root, "Autodesk Revit *"))
        {
            string journals = System.IO.Path.Combine(versionDir, "Journals");
            if (!Directory.Exists(journals))
                continue;
            string version = System.IO.Path.GetFileName(versionDir)["Autodesk Revit ".Length..];
            result.AddRange(FindInFolder(journals, version));
        }
        return [.. result.OrderByDescending(j => j.LastWrite)];
    }

    public static IEnumerable<JournalFile> FindInFolder(string folder, string revitVersion = "")
    {
        foreach (string file in Directory.EnumerateFiles(folder, "journal.*.txt"))
        {
            var m = MainJournalName().Match(System.IO.Path.GetFileName(file));
            if (!m.Success)
                continue;
            // A crash copy only stands in for a journal that is gone; when both exist the journal itself is read.
            if (m.Groups[2].Success && File.Exists(System.IO.Path.Combine(folder, $"journal.{m.Groups[1].Value}.txt")))
                continue;
            var info = new FileInfo(file);
            yield return new JournalFile(file, revitVersion, int.Parse(m.Groups[1].Value), info.LastWriteTime, info.Length);
        }
    }
}
