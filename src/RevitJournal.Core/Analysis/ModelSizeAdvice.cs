using System.Globalization;
using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>The model a Revit session has open and what Autodesk's guidance says about it.</summary>
/// <param name="LinksMb">Size on disk of the <paramref name="LinksKnown"/> linked files found on this computer.</param>
/// <param name="NeededRam">Autodesk's 20 x the model and its known links, e.g. "about 22.0 GB of RAM".</param>
/// <param name="IsCloud">A Revit cloud model: its local copy lives in the CollaborationCache.</param>
public sealed record ModelLoad(string Name, int ModelMb, int? Elements, int LinkCount, int LinksKnown, int LinksMb, string NeededRam, bool OverGuidance,
    bool IsCloud = false);

/// <summary>
/// The size of the models on disk against the memory of the computer. Autodesk's Memory Recommendations: the RAM
/// required is "approximately 20 times the size of your compacted central project file". Autodesk's support article on
/// the warning "Your current session is using more memory than your computer can efficiently support" counts the links
/// too: the warning is triggered "if the amount of RAM available before launching Revit is less than 20 times the
/// combined size of RVT files being opened (including linked files)". So the figure is 20 x (model + the links found on
/// this computer); links whose files are not here cannot be counted, and the facts say so.
/// </summary>
public static class ModelSizeAdvice
{
    public const string Rule = "metric-model-size-vs-ram";

    /// <summary>Autodesk, Memory Recommendations: RAM is approximately this many times the size of the central file.</summary>
    private const int Factor = 20;
    private const int Mb = 1024 * 1024;

    /// <summary>What a Revit has open, for the live overview: the model, its links, and Autodesk's figure for it. Null until the model's size is known.</summary>
    public static ModelLoad? Load(JournalSession s, Func<string, long?>? fileSize, int? ramMb)
    {
        if (Host(s, fileSize) is not { Bytes: { } bytes } host)
            return null;
        int modelMb = (int)(bytes / Mb);
        var links = Links(s, fileSize);
        var known = links.Where(l => l.Bytes is not null).ToList();
        int linksMb = (int)(known.Sum(l => l.Bytes!.Value) / Mb);
        bool over = ramMb is { } ram && Required(modelMb + linksMb) > ram;
        bool cloud = host.Path.Contains("CollaborationCache", StringComparison.OrdinalIgnoreCase);
        return new ModelLoad(Name(s, host), modelMb, host.Elements, links.Count, known.Count, linksMb, Needed(modelMb + linksMb), over, cloud);
    }

    /// <summary>
    /// All models open in all running Revit sessions together, with their known links: Autodesk's 20 x their combined
    /// size. Null when fewer than two models are known or the total still fits.
    /// </summary>
    public static (int TotalMb, string NeededRam)? Combined(IReadOnlyCollection<ModelLoad> loads, long ramMb)
    {
        if (loads.Count < 2 || ramMb <= 0)
            return null;
        int total = loads.Sum(l => l.ModelMb + l.LinksMb);
        return Required(total) > ramMb ? (total, Needed(total)) : null;
    }

    public static List<string>? Facts(JournalSession s, AnalysisContext ctx)
    {
        if (Host(s, ctx.FileSize) is not { Bytes: { } bytes } host || s.TotalRamMb is not { } ramMb)
            return null;
        int modelMb = (int)(bytes / Mb);
        var links = Links(s, ctx.FileSize);
        var known = links.Where(l => l.Bytes is not null).OrderByDescending(l => l.Bytes).ToList();
        int linksMb = (int)(known.Sum(l => l.Bytes!.Value) / Mb);
        bool overModel = Required(modelMb) > ramMb, overWithLinks = Required(modelMb + linksMb) > ramMb;
        if (!overWithLinks)
            return null;

        var facts = new List<string>
        {
            $"{Name(s, host)} is {Fmt.Mb(modelMb)} on disk" + (host.Elements is { } e ? $" ({e.ToString("N0", CultureInfo.InvariantCulture)} elements)" : "") +
            $"; this computer has {Fmt.Mb(ramMb)} of RAM.",
            $"Autodesk's Memory Recommendations: the RAM required is approximately 20 times the size of the compacted central file, here {Needed(modelMb)}" +
            (overModel ? ", more than this computer has." : "."),
        };

        if (links.Count > 0)
        {
            facts.Add(known.Count == 0
                ? $"The session also loaded {links.Count} linked model(s); their sizes are not known here because the files are not on this computer, so they are not counted."
                : $"The session also loaded {links.Count} linked model(s); {known.Count} of them are on this computer and take {Fmt.Mb(linksMb)} on disk together " +
                  $"(largest: {string.Join(", ", known.Take(3).Select(l => $"{l.Name} {Fmt.Mb((int)(l.Bytes!.Value / Mb))}"))}).");
            if (known.Count > 0)
                facts.Add($"Autodesk counts linked files too: Revit warns about memory when the RAM available is less than 20 times the combined size of the RVT files opened, including linked files. " +
                          $"With these links ({Fmt.Mb(modelMb + linksMb)} together) that is {Needed(modelMb + linksMb)}" + (overWithLinks ? ", more than this computer has." : "."));
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
            AutodeskArticle.PerformanceNote.Cite("Unload links that are not needed in the view and reload them when required."),
            AutodeskArticle.MemoryInstability.Cite("Limit the worksets you open: in the Open dialog choose Specify from the Open drop-down list and close the worksets you do not need."),
            AutodeskArticle.MemoryInstability.Cite("Limit the number of open views: View tab > Windows panel > Close Inactive."),
            AutodeskArticle.MemoryInstability.Cite("Purge unused objects."),
            AutodeskArticle.MemoryRecommendations.Cite("Restart Revit regularly, and before printing, rendering, exporting or upgrading models."),
        };
        if (Host(s, ctx.FileSize) is { Bytes: { } bytes })
        {
            int total = (int)(bytes / Mb) + (int)(Links(s, ctx.FileSize).Sum(l => l.Bytes ?? 0) / Mb);
            steps.Add(AutodeskArticle.MemoryHighUsage.Cite("If the system supports more RAM, adding RAM can clear the memory warning. " +
                $"For this model with its links Autodesk's figure is {Needed(total)}."));
        }
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

    /// <summary>RAM in MB for files of this size on disk: Autodesk's 20 x.</summary>
    public static long Required(int modelMb) => (long)Factor * modelMb;

    private static string Needed(int modelMb) => $"about {Fmt.Mb((int)Math.Min(Required(modelMb), int.MaxValue))} of RAM";
}
