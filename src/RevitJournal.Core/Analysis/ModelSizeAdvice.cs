using System.Globalization;
using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>
/// The size of the model on disk against the memory of the computer, by Autodesk's Memory Recommendations: the RAM
/// required is "approximately 20 times the size of your compacted central project file". Autodesk gives the figure for
/// the project file only. Linked models also take memory (Autodesk lists unloading them among the ways to save it), so
/// the same factor is applied to the links found on this computer as our estimate, and always labelled as such.
/// </summary>
/// <summary>The model a Revit session has open and what Autodesk's guidance says about it.</summary>
/// <param name="LinksMb">Size of the <paramref name="LinksKnown"/> linked files found on this computer.</param>
/// <param name="NeededRam">20 x (model + known links), e.g. "about 22.0 GB of RAM": Autodesk's figure when no link is known, otherwise our estimate.</param>
public sealed record ModelLoad(string Name, int ModelMb, int? Elements, int LinkCount, int LinksKnown, int LinksMb, string NeededRam, bool OverGuidance);

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
        return new ModelLoad(Name(s, host), modelMb, host.Elements, links.Count, known.Count, linksMb, Needed(modelMb + linksMb), over);
    }

    /// <summary>
    /// All models open in all running Revit sessions together, with their known links, against 20 x their total size
    /// (our estimate on Autodesk's factor). Null when fewer than two models are known or the total still fits.
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
        if (!overModel && !overWithLinks)
            return null;

        var facts = new List<string>
        {
            $"{Name(s, host)} is {Fmt.Mb(modelMb)} on disk" + (host.Elements is { } e ? $" ({e.ToString("N0", CultureInfo.InvariantCulture)} elements)" : "") +
            $"; this computer has {Fmt.Mb(ramMb)} of RAM.",
            $"Autodesk's Memory Recommendations: the RAM required is approximately 20 times the size of the compacted central file, here {Needed(modelMb)}" + (overModel ? ", more than this computer has." : "."),
        };

        if (links.Count > 0)
        {
            facts.Add(known.Count == 0
                ? $"The session also loaded {links.Count} linked model(s); their sizes are not known here because the files are not on this computer. Autodesk's figure is for the project file alone."
                : $"The session also loaded {links.Count} linked model(s); {known.Count} of them are on this computer and take {Fmt.Mb(linksMb)} on disk together " +
                  $"(largest: {string.Join(", ", known.Take(3).Select(l => $"{l.Name} {Fmt.Mb((int)(l.Bytes!.Value / Mb))}"))}).");
            if (known.Count > 0)
                facts.Add($"Estimate, not an Autodesk figure: the same factor applied to the model and these links ({Fmt.Mb(modelMb + linksMb)} together) gives {Needed(modelMb + linksMb)}" +
                          (overWithLinks ? ", more than this computer has." : "."));
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
        {
            int modelMb = (int)(bytes / Mb);
            int linksMb = (int)(Links(s, ctx.FileSize).Sum(l => l.Bytes ?? 0) / Mb);
            steps.Add(AutodeskArticle.MemoryRecommendations.Cite("Ask IT about more memory: inadequate RAM can significantly hurt model performance. " +
                $"A model of this size needs {Needed(modelMb)}" + (linksMb > 0 ? $"; with its links, by our estimate, {Needed(modelMb + linksMb)}." : ".")));
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
