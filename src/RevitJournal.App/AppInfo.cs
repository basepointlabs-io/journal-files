using System.Reflection;

namespace RevitJournal.App;

public static class AppInfo
{
    /// <summary>
    /// The version to show a person, e.g. "0.3.0", from Directory.Build.props. The SDK appends "+&lt;commit&gt;" to the
    /// informational version on some builds; that belongs in a log, not in the title bar (same rule as FlexiData's VersionInfo).
    /// </summary>
    public static string Version { get; } = Display(typeof(AppInfo).Assembly);

    private static string Display(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string numeric = assembly.GetName().Version?.ToString(3) ?? "";
        if (string.IsNullOrWhiteSpace(informational))
            return numeric;
        int plus = informational.IndexOf('+');
        string trimmed = (plus >= 0 ? informational[..plus] : informational).Trim();
        return trimmed.Length > 0 ? trimmed : numeric;
    }
}
