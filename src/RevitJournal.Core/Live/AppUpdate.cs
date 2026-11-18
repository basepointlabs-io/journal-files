using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RevitJournal.Core.Live;

/// <summary>A published release of Revit Doctor on GitHub.</summary>
/// <param name="Notes">The release notes (Markdown), shown as "What's new".</param>
/// <param name="SetupUrl">The installer of this release; null when the release has none.</param>
/// <param name="ChecksumsUrl">SHA256SUMS of the release; without it the installer is not installed automatically.</param>
public sealed record AppRelease(Version Version, string Tag, string Notes, string PageUrl, string? SetupUrl, string? SetupName, string? ChecksumsUrl);

/// <summary>
/// Finds out whether a newer Revit Doctor is published, and fetches its installer. Reads only the public GitHub release
/// page of this project: nothing about the computer, the user or the models is sent. Without a connection nothing
/// happens and the app works as before.
/// </summary>
public static class AppUpdate
{
    public const string Repository = "basepointlabs-io/journal-files";
    public const string LatestUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>Only files of this project's own releases are downloaded.</summary>
    public const string DownloadPrefix = "https://github.com/" + Repository + "/releases/download/";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static async Task<AppRelease?> FetchLatestAsync(HttpClient http, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return Parse(await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads GitHub's "latest release" answer; drafts, pre-releases and tags that are not "vX.Y.Z" are ignored.</summary>
    public static AppRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft") || Bool(root, "prerelease"))
            return null;
        if (Str(root, "tag_name") is not { } tag || VersionOf(tag) is not { } version)
            return null;

        string? setupUrl = null, setupName = null, checksums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                if (Str(a, "name") is not { } name || Str(a, "browser_download_url") is not { } url || !url.StartsWith(DownloadPrefix, StringComparison.Ordinal))
                    continue;
                if (name.Equals($"RevitDoctor-{version.ToString(3)}-setup.exe", StringComparison.OrdinalIgnoreCase))
                    (setupUrl, setupName) = (url, name);
                else if (name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                    checksums = url;
            }
        }
        return new AppRelease(version, tag, Str(root, "body") ?? "", Str(root, "html_url") ?? ReleasesPage, setupUrl, setupName, checksums);
    }

    /// <summary>"v0.8.0" or "0.8.0" → 0.8.0; anything else → null.</summary>
    public static Version? VersionOf(string tag)
    {
        string s = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return Version.TryParse(s, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    /// <summary>True when <paramref name="release"/> is newer than the running version (compared as X.Y.Z).</summary>
    public static bool IsNewer(AppRelease release, Version current) =>
        release.Version > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    /// <summary>"&lt;sha256&gt;  &lt;file name&gt;" lines (sha256sum format) → file name → lowercase hash.</summary>
    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space != 64)
                continue;
            string hash = line[..64].ToLowerInvariant(), name = line[space..].Trim().TrimStart('*');
            if (hash.All(Uri.IsHexDigit) && name.Length > 0)
                result[name] = hash;
        }
        return result;
    }

    /// <summary>
    /// Downloads the release's installer into <paramref name="folder"/> and checks it against the release's SHA256SUMS.
    /// Returns the installer's path; throws <see cref="InvalidDataException"/> when the release has no installer or
    /// checksum, or the file does not match (the file is then deleted).
    /// </summary>
    public static async Task<string> DownloadSetupAsync(HttpClient http, AppRelease release, string folder, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        if (release.SetupUrl is null || release.SetupName is null || release.ChecksumsUrl is null)
            throw new InvalidDataException("This release has no installer with a checksum.");

        string sums = await http.GetStringAsync(release.ChecksumsUrl, cancel).ConfigureAwait(false);
        if (!ParseChecksums(sums).TryGetValue(release.SetupName, out var expected))
            throw new InvalidDataException($"SHA256SUMS of {release.Tag} does not list {release.SetupName}.");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, release.SetupName);
        using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total.Value);
            }
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(path);
            throw new InvalidDataException($"{release.SetupName} does not match its checksum; it was not installed.");
        }
        return path;
    }

    /// <summary>"What's new" for a notification or a band: the release notes as plain lines, without Markdown and links.</summary>
    public static string Summary(AppRelease release, int maxLength = 600)
    {
        int start = release.Notes.IndexOf("## What's new", StringComparison.OrdinalIgnoreCase);
        string notes = start >= 0 ? release.Notes[start..] : release.Notes;
        int end = notes.IndexOf("\n## Download", StringComparison.OrdinalIgnoreCase);
        if (end > 0)
            notes = notes[..end];
        var lines = notes.Replace("\r", "").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"\*\*|`|^#+\s*|\[([^\]]*)\]\([^)]*\)", "$1").Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("What's new", StringComparison.OrdinalIgnoreCase));
        string text = string.Join("\n", lines);
        return text.Length > maxLength ? text[..(maxLength - 1)].TrimEnd() + "…" : text;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
