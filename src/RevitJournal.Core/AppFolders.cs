namespace RevitJournal.Core;

/// <summary>Where the app keeps its own files, in the user's profile. Nothing here leaves the computer.</summary>
public static class AppFolders
{
    /// <summary>The program was called Revit Journal Analyzer up to 0.4.0 and kept its files under that name.</summary>
    private const string FormerName = "RevitJournalAnalyzer";
    private static readonly string[] Carried = ["settings.json", "disk-history.csv"];

    private static readonly Lazy<string> Folder = new(() =>
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(root, "RevitDoctor"), former = Path.Combine(root, FormerName);
        try
        {
            // Copied, not moved: a copy of the old version may still be running and using its folder.
            if (!Directory.Exists(current) && Directory.Exists(former))
            {
                Directory.CreateDirectory(current);
                foreach (string name in Carried)
                {
                    if (File.Exists(Path.Combine(former, name)))
                        File.Copy(Path.Combine(former, name), Path.Combine(current, name));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting with fresh settings is fine.
        }
        return current;
    });

    /// <summary>%LOCALAPPDATA%\RevitDoctor: settings, the result cache and the disk-space history.</summary>
    public static string Data => Folder.Value;
}
