using RevitJournal.Core.Model;

namespace RevitJournal.Core.Analysis;

/// <summary>Concrete recovery steps for failed Synchronize with Central, using the model name and times from the journal.</summary>
public static class SyncAdvice
{
    public static List<string> Steps(JournalSession s)
    {
        var failed = s.Syncs.Where(x => x.Finished && !x.Succeeded).ToList();
        if (failed.Count == 0)
            return [];

        var model = s.Models.FirstOrDefault(m => m.Worksharing?.Contains("Local", StringComparison.OrdinalIgnoreCase) == true) ?? s.Models.FirstOrDefault();
        string name = model is null ? "the model" : Fmt.FileName(model.Path);
        bool cloud = model?.Path.StartsWith("Autodesk Docs://", StringComparison.OrdinalIgnoreCase) == true
                     || model?.Worksharing?.Contains("Cloud", StringComparison.OrdinalIgnoreCase) == true;
        var lastOk = s.Syncs.LastOrDefault(x => x.Succeeded);
        DateTime since = lastOk?.End ?? s.Opens.FirstOrDefault()?.Start ?? s.Start ?? failed[0].Start;
        TimeSpan unsynced = (s.LastTimestamp ?? failed[^1].Start) - since;

        var steps = new List<string>
        {
            $"In the Revit window with {name}, click Save now (not Synchronize). Your changes since {Fmt.Time(since)} ({Fmt.Duration(unsynced)} of work) are only in your local copy; Save keeps them there.",
            "Close Revit completely (File > Exit), not just the model, so the next attempt starts in a fresh Revit process.",
            cloud
                ? $"Start Revit and open {name} again from Autodesk Docs (Home > the project). Revit opens your local copy with your changes."
                : $"Start Revit and open your local file of {name} again (not the central file).",
            "Run Synchronize with Central once.",
            "If it fails again with the same error, follow the first finding below for that error, and keep saving locally meanwhile.",
            "Do not use Detach, \"Close and discard changes\" or a new local copy while sync is failing: they throw away the work that exists only in your local copy. Ask your BIM manager first.",
        };
        return steps;
    }
}
