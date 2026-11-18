using System.Globalization;
using RevitJournal.Core.Analysis;

namespace RevitJournal.Core.Live;

/// <summary>One resource of this computer against what Autodesk asks for, with the article that says so.</summary>
/// <param name="Value">What this computer has, e.g. "47.9 GB".</param>
/// <param name="Autodesk">Autodesk's figure, e.g. "64 GB for a model up to about 1 GB (Performance)".</param>
/// <param name="Note">How the two compare, in a few words; empty when they match.</param>
public sealed record RequirementCheck(string Item, string Value, string Autodesk, HealthLevel Level, AutodeskArticle Source, string Note = "");

/// <summary>
/// This computer against Autodesk's published figures, for the Revit versions that are running. Only Autodesk's own
/// numbers are used here; the thresholds the app sets itself (free RAM, virtual memory, GDI) live in
/// <see cref="HealthMonitor"/> and say so.
/// <list type="bullet">
/// <item>RAM per model size: system requirements for Revit 2025 and 2026 (16 GB ~300 MB, 32 GB ~600 MB, 64 GB ~1 GB).</item>
/// <item>Free disk: 30 GB (2025, and "Minimum 30 GB free space is required" in the article on Revit closing unexpectedly);
/// 2026 also recommends 100 GB free for temporary files.</item>
/// <item>Cloud models: 20 x the model plus 3 x all its links free on the drive with the cache (article on cloud models
/// reported as corrupt).</item>
/// <item>Page file: at least Windows' recommendation and at most twice the installed RAM (Optimize your Operating System).</item>
/// <item>Video memory: 4 GB (2025; 2026 Minimum), 6 GB (2026 Value), 8 GB (2026 Performance).</item>
/// <item>Revit Personal Accelerator always running for cloud models (system requirements, Revit Cloud Worksharing).</item>
/// </list>
/// </summary>
public static class ComputerRequirements
{
    private const long Gb = 1024;

    /// <summary>Windows reports a little less than the installed RAM (memory reserved by the hardware): 64 GB shows as about 63.7.</summary>
    private const double Installed = 0.95;

    private sealed record RamTier(string Name, long RamMb, int ModelMb);

    private static readonly RamTier[] Tiers =
    [
        new("Minimum", 16 * Gb, 300),
        new("Value", 32 * Gb, 600),
        new("Performance", 64 * Gb, 1024),
    ];

    public const long DiskRequiredMb = 30 * Gb;
    public const long DiskRecommended2026Mb = 100 * Gb;
    public const long VideoMinimumMb = 4 * Gb;

    public static IReadOnlyList<RequirementCheck> Check(SystemSample now, IReadOnlyCollection<ModelLoad>? models)
    {
        bool is2026 = now.Revits.Any(r => int.TryParse(r.Version, NumberStyles.None, CultureInfo.InvariantCulture, out int y) && y >= 2026);
        var requirements = is2026 ? AutodeskArticle.SystemRequirements2026 : AutodeskArticle.SystemRequirements;
        string version = is2026 ? "Revit 2026" : "Revit 2025";
        var rows = new List<RequirementCheck>();
        var loads = models?.ToList() ?? [];

        if (now.TotalRamMb > 0)
            rows.Add(Ram(now.TotalRamMb, loads, requirements));

        var profile = now.Disks.FirstOrDefault(d => string.Equals(d.Name, now.ProfileDrive, StringComparison.OrdinalIgnoreCase)) ?? now.Disks.FirstOrDefault();
        if (profile is not null)
        {
            rows.Add(Disk(profile, is2026, version, requirements));
            if (loads.Where(l => l.IsCloud).ToList() is { Count: > 0 } cloud)
                rows.Add(CloudDisk(profile, cloud));
        }

        if (now.TotalRamMb > 0 && now.CommitLimitMb > now.TotalRamMb)
            rows.Add(PageFile(now.CommitLimitMb - now.TotalRamMb, now.TotalRamMb));

        if (now.VideoMemoryMb is { } video)
            rows.Add(new RequirementCheck("Video memory", Fmt.Mb((int)video),
                is2026 ? "4 GB (Minimum), 6 GB (Value), 8 GB (Performance)" : "at least 4 GB",
                video < VideoMinimumMb * Installed ? HealthLevel.Watch : HealthLevel.Ok, requirements,
                video < VideoMinimumMb * Installed ? "less than Autodesk asks for" : ""));

        if (now.AcceleratorRunning is { } running && loads.Any(l => l.IsCloud))
            rows.Add(new RequirementCheck("Revit Personal Accelerator", running ? "running" : "not running",
                "always running on computers that open cloud models", running ? HealthLevel.Ok : HealthLevel.Watch, requirements,
                running ? "" : "start it, or check that it starts with Windows (Task Manager > Startup apps)"));

        return rows;
    }

    /// <summary>
    /// The RAM Autodesk's table gives for the largest open model; with no model known, the table itself. A model above
    /// the largest tier (1 GB) is beyond what Autodesk publishes: 64 GB is the floor, and the row says so.
    /// </summary>
    private static RequirementCheck Ram(long ramMb, List<ModelLoad> loads, AutodeskArticle source)
    {
        string have = Fmt.Mb((int)ramMb);
        if (loads.Count == 0)
        {
            var below = ramMb < Tiers[0].RamMb * Installed;
            return new RequirementCheck("RAM", have, "16 GB for a model up to ~300 MB, 32 GB up to ~600 MB, 64 GB up to ~1 GB",
                below ? HealthLevel.Watch : HealthLevel.Ok, source, below ? "less than Autodesk's minimum" : "");
        }

        var largest = loads.MaxBy(l => l.ModelMb)!;
        var tier = Tiers.FirstOrDefault(t => largest.ModelMb <= t.ModelMb) ?? Tiers[^1];
        bool beyond = largest.ModelMb > Tiers[^1].ModelMb;
        bool enough = ramMb >= tier.RamMb * Installed;
        string autodesk = beyond
            ? $"{Fmt.Mb((int)tier.RamMb)} or more: {largest.Name} ({Fmt.Mb(largest.ModelMb)}) is larger than the ~1 GB of Autodesk's largest configuration"
            : $"{Fmt.Mb((int)tier.RamMb)} ({tier.Name}) for {largest.Name}, {Fmt.Mb(largest.ModelMb)}: up to ~{Fmt.Mb(tier.ModelMb)}";
        string note = !enough ? $"{Fmt.Mb((int)(tier.RamMb - ramMb))} short of Autodesk's figure"
            : beyond ? "Autodesk gives no figure for models this large" : "";
        return new RequirementCheck("RAM", have, autodesk, enough && !beyond ? HealthLevel.Ok : HealthLevel.Watch, source, note);
    }

    private static RequirementCheck Disk(DiskSample d, bool is2026, string version, AutodeskArticle source)
    {
        string autodesk = is2026 ? "30 GB free; 100 GB recommended for temporary files" : "30 GB free";
        var level = d.FreeMb < DiskRequiredMb ? HealthLevel.Watch : HealthLevel.Ok;
        string note = d.FreeMb < DiskRequiredMb ? $"below what {version} requires"
            : is2026 && d.FreeMb < DiskRecommended2026Mb ? "below the 100 GB Autodesk recommends" : "";
        return new RequirementCheck($"Free disk {d.Name}", Fmt.Mb((int)Math.Min(d.FreeMb, int.MaxValue)), autodesk, level, source, note);
    }

    /// <summary>Autodesk: 20 x the model (unpacked to the temp folder on open) plus 3 x all linked RVT models (the cloud cache).</summary>
    public static long CloudSpaceMb(IEnumerable<ModelLoad> cloud) => cloud.Sum(l => 20L * l.ModelMb + 3L * l.LinksMb);

    private static RequirementCheck CloudDisk(DiskSample d, List<ModelLoad> cloud)
    {
        long need = CloudSpaceMb(cloud);
        bool linksUnknown = cloud.Any(l => l.LinkCount > l.LinksKnown);
        string what = cloud.Count == 1 ? cloud[0].Name : $"{cloud.Count} cloud models";
        bool short_ = d.FreeMb < need;
        return new RequirementCheck($"Free disk {d.Name} for cloud models", Fmt.Mb((int)Math.Min(d.FreeMb, int.MaxValue)),
            $"{Fmt.Mb((int)Math.Min(need, int.MaxValue))} for {what}: 20 × the model + 3 × its links",
            short_ ? HealthLevel.Watch : HealthLevel.Ok, AutodeskArticle.CloudModelCorrupt,
            (short_ ? $"{Fmt.Mb((int)Math.Min(need - d.FreeMb, int.MaxValue))} short" : "") +
            (linksUnknown ? (short_ ? "; " : "") + "links not found on this computer are not counted" : ""));
    }

    private static RequirementCheck PageFile(long pageMb, long ramMb)
    {
        bool over = pageMb > 2 * ramMb;
        return new RequirementCheck("Page file", Fmt.Mb((int)pageMb),
            $"at least Windows' recommended size, at most 2 × RAM ({Fmt.Mb((int)(2 * ramMb))})",
            over ? HealthLevel.Watch : HealthLevel.Ok, AutodeskArticle.OptimizeOs,
            over ? "larger than Autodesk advises: system performance can degrade" : "");
    }
}
