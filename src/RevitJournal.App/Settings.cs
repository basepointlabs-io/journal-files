using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RevitJournal.App;

/// <summary>Per-user settings in %LOCALAPPDATA%\RevitJournalAnalyzer; nothing leaves the computer.</summary>
public sealed class Settings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitJournalAnalyzer");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    /// <summary>Where analysis results of finished sessions are kept between starts.</summary>
    public static string CacheFolder => Path.Combine(Folder, "cache");

    /// <summary>The tray hint ("still watching in the background") is shown once.</summary>
    public bool TrayHintShown { get; set; }

    public static Settings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them must not stop the app.
        }
    }

    // ---- start with Windows (HKCU Run key, no admin rights needed) ----

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RevitJournalAnalyzer";

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value && Environment.ProcessPath is { } exe)
                key.SetValue(RunValue, $"\"{exe}\" --background");
            else
                key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }
}
