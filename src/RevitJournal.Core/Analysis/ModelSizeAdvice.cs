using System.Globalization;
using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// The size of the model on disk against the memory of the computer, by Autodesk's two published measures: the table
/// in the system requirements (a single model of about 300 MB / 600 MB / 1 GB for 16 / 32 / 64 GB of RAM) and
/// "approximately 20 times the size of the compacted central file". Both are estimates for one model; linked models
/// come on top, and their sizes are read from this computer's disk where the files are.
/// </summary>
/// <summary>The model a Revit session has open and what Autodesk's guidance says about it.</summary>
/// <param name="LinksMb">Size of the <paramref name="LinksKnown"/> linked files found on this computer.</param>
/// <param name="NeededRam">Autodesk's figure for a single model of this size, e.g. "64 GB of RAM".</param>
public sealed record ModelLoad(string Name, int ModelMb, int? Elements, int LinkCount, int LinksKnown, int LinksMb, string NeededRam, bool OverGuidance);

public static class ModelSizeAdvice
{
    public const string Rule = "metric-model-size-vs-ram";

    /// <summary>Autodesk, System requirements for Revit 2025: RAM in GB and the single-model size in MB it is "usually sufficient" for.</summary>
    private static readonly (int RamGb, int ModelMb)[] Table = [(16, 300), (32, 600), (64, 1024)];
    /// <summary>The sizes are "approximately": a model this much over the figure is not reported.</summary>
    private const double Tolerance = 1.1;
    private const int Mb = 1024 * 1024;

    /// <summary>What a Revit has open, for the live overview: the model, its links, and Autodesk's figure for it. Null until the model's size is known.</summary>
    public static ModelLoad? Load(JournalSession s, Func<string, long?>? fileSize, int? ramMb)
    {
        if (Host(s, fileSize) is not { Bytes: { } bytes } host)
            return null;
        int modelMb = (int)(bytes / Mb);
        var links = Links(s, fileSize);
        var known = links.Where(l => l.Bytes is not null).ToList();
        bool over = ramMb is { } ram && (modelMb > Tier(ram).ModelMb * Tolerance || 20L * modelMb > ram);
        return new ModelLoad(Name(s, host), modelMb, host.Elements, links.Count, known.Count, (int)(known.Sum(l => l.Bytes!.Value) / Mb), Needed(modelMb), over);
    }

    /// <summary>
    /// All models open in all running Revit sessions together, against Autodesk's figure for a single model of that
    /// total size. Null when fewer than two models are known or the total still fits.
    /// </summary>
    public static (int TotalMb, string NeededRam)? Combined(IReadOnlyCollection<ModelLoad> loads, long ramMb)
    {
        if (loads.Count < 2 || ramMb <= 0)
            return null;
        int total = loads.Sum(l => l.ModelMb);
        bool over = total > Tier((int)Math.Min(ramMb, int.MaxValue)).ModelMb * Tolerance || 20L * total > ramMb;
        return over ? (total, Needed(total)) : null;
    }

    public static List<string>? Facts(JournalSession s, AnalysisContext ctx)
    {
        if (Host(s, ctx.FileSize) is not { Bytes: { } bytes } host || s.TotalRamMb is not { } ramMb)
            return null;
        int modelMb = (int)(bytes / Mb);
        var tier = Tier(ramMb);
        bool overTable = modelMb > tier.ModelMb * Tolerance;
        long twentyTimesMb = 20L * modelMb;
        bool overTwenty = twentyTimesMb > ramMb;
        if (!overTable && !overTwenty)
            return null;

        var facts = new List<string>
        {
            $"{Name(s, host)} is {Fmt.Mb(modelMb)} on disk" + (host.Elements is { } e ? $" ({e.ToString("N0", CultureInfo.InvariantCulture)} elements)" : "") +
            $"; this computer has {Fmt.Mb(ramMb)} of RAM.",
            $"Autodesk's system requirements for Revit 2025: {tier.RamGb} GB of RAM is usually sufficient for a single model up to approximately {Fmt.Mb(tier.ModelMb)} on disk; a model of this size is listed under {Needed(modelMb)}.",
            $"Autodesk's Memory Recommendations: the RAM required is approximately 20 times the size of the compacted central file, here {Fmt.Mb((int)Math.Min(twentyTimesMb, int.MaxValue))}" +
            (overTwenty ? ", more than this computer has." : "."),
        };

        var links = Links(s, ctx.FileSize);
        if (links.Count > 0)
        {
            var known = links.Where(l => l.Bytes is not null).OrderByDescending(l => l.Bytes).ToList();
            facts.Add(known.Count == 0
                ? $"The session also loaded {links.Count} linked model(s); their sizes are not known here because the files are not on this computer. Autodesk's figures are for a single model."
                : $"The session also loaded {links.Count} linked model(s); {known.Count} of them are on this computer and take {Fmt.Mb((int)(known.Sum(l => l.Bytes!.Value) / Mb))} on disk together " +
                  $"(largest: {string.Join(", ", known.Take(3).Select(l => $"{l.Name} {Fmt.Mb((int)(l.Bytes!.Value / Mb))}"))}). Autodesk's figures are for a single model.");
        }

        if (Metrics.PeakRevitRam(s) is { } peak && s.Ram.Count > 0)
            facts.Add($"Measured in this session: Revit used up to {Fmt.Mb(peak)}; the lowest free memory was {Fmt.Mb(s.Ram.Min(r => r.AvailableMb))}.");
        return facts;
    }

    public static List<string> Steps(JournalSession s, AnalysisContext ctx)
    {
        var largest = Links(s, ctx.FileSize).Where(l => l.Bytes is not null).OrderByDescending(l => l.Bytes).Take(3).Select(l => l.Name).ToList();
        var steps = new List<string>
        {
            AutodeskArticle.MemoryInstability.Cite("Reduce the number of loaded DWG and RVT links" +
                                                   (largest.Count > 0 ? $"; the largest linked models in this session were {string.Join(", ", largest)}." : ".")),
            AutodeskArticle.MemoryInstability.Cite("Limit the worksets you open: in the Open dialog choose Specify from the Open drop-down list and close the worksets you do not need."),
            AutodeskArticle.MemoryInstability.Cite("Limit the number of open views: View tab > Windows panel > Close Inactive."),
            AutodeskArticle.MemoryInstability.Cite("Purge unused objects."),
            AutodeskArticle.MemoryRecommendations.Cite("Restart Revit regularly, and before printing, rendering, exporting or upgrading models."),
        };
        if (Host(s, ctx.FileSize) is { Bytes: { } bytes })
            steps.Add(AutodeskArticle.SystemRequirements.Cite($"Ask IT about more memory: Autodesk lists a single model of this size under {Needed((int)(bytes / Mb))}."));
        return steps;
    }

    /// <summary>
    /// The edited model: the largest of the session's own files. Revit logs the size only at a save or sync; before
    /// that it is read from the local file on this computer.
    /// </summary>
    private static ModelFile? Host(JournalSession s, Func<string, long?>? fileSize) =>
        s.ModelFiles.Where(m => !m.IsLink)
            .Select(m => m.Bytes is not null ? m : m with { Bytes = fileSize?.Invoke(m.Path) })
            .Where(m => m.Bytes is not null)
            .MaxBy(m => m.Bytes);

    private static List<(string Name, long? Bytes)> Links(JournalSession s, Func<string, long?>? fileSize) =>
        [.. s.ModelFiles.Where(m => m.IsLink).Select(m => (Name(s, m), m.Bytes ?? fileSize?.Invoke(m.Path)))];

    /// <summary>Cloud models are cached under a GUID file name; the journal maps it to the real name.</summary>
    private static string Name(JournalSession s, ModelFile m) => s.DocumentName(Fmt.FileName(m.Path));

    /// <summary>The row of Autodesk's table this computer falls in (a "32 GB" machine reports a little less).</summary>
    private static (int RamGb, int ModelMb) Tier(int ramMb) =>
        Table.LastOrDefault(t => ramMb >= t.RamGb * 1024 * 0.93) is { RamGb: > 0 } row ? row : Table[0];

    private static string Needed(int modelMb) =>
        Table.FirstOrDefault(t => modelMb <= t.ModelMb * Tolerance) is { RamGb: > 0 } row
            ? $"{row.RamGb} GB of RAM"
            : "64 GB of RAM or more (the table ends at approximately 1 GB)";
}
