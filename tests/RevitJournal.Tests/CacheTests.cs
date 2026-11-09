using System.Text;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Symptoms;

namespace RevitJournal.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rja-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _journal;
    private readonly Analyzer _analyzer = Analyzer.CreateDefault();

    public CacheTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = Path.Combine(_dir, "journal.0001.txt");
        _analyzer.Cache = new AnalysisCache(Path.Combine(_dir, "cache"), _analyzer.Rules);
    }

    private static void Write(string path, params string[] blocks) =>
        File.WriteAllText(path, string.Join("\r\n", Fixtures.Lines(blocks)) + "\r\n", Encoding.Latin1);

    [Fact]
    public void A_finished_journal_is_read_once_and_served_from_disk_until_it_changes()
    {
        Write(_journal, Fixtures.Header, Fixtures.FailedCloudSync, Fixtures.GoodSync, Fixtures.CrashRecord);

        var first = _analyzer.AnalyzeFile(_journal);
        Assert.Equal(0, _analyzer.Cache!.Hits);
        var second = _analyzer.AnalyzeFile(_journal);
        Assert.Equal(1, _analyzer.Cache.Hits);

        // The cached result says the same thing, finding by finding.
        Assert.Equal(first.Headline, second.Headline);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(EndKind.Crash, second.EndKind);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Findings.Select(f => (f.Rule.Id, f.Severity, f.HitCount)), second.Findings.Select(f => (f.Rule.Id, f.Severity, f.HitCount)));
        Assert.Equal(first.Findings.SelectMany(f => f.Steps), second.Findings.SelectMany(f => f.Steps));
        Assert.Equal(first.Findings.SelectMany(f => f.Evidence), second.Findings.SelectMany(f => f.Evidence));

        // ...and still answers the symptom questions, which read the session.
        var advisor = SymptomAdvisor.CreateDefault(_analyzer.Rules);
        var sync = advisor.Diagnose(advisor.Symptoms.Single(s => s.Id == "wont-sync"), [second], new DateTime(2026, 11, 6, 9, 0, 0));
        Assert.StartsWith("1 of 2 sync attempts failed", sync.Headline);

        // Asking for a fresh read bypasses the cache; a changed file invalidates it.
        _analyzer.AnalyzeFile(_journal, useCache: false);
        Assert.Equal(1, _analyzer.Cache.Hits);
        File.AppendAllText(_journal, "' 0:< one more line \r\n", Encoding.Latin1);
        Assert.Equal(first.Session.LineCount + 1, _analyzer.AnalyzeFile(_journal).Session.LineCount);
        Assert.Equal(1, _analyzer.Cache.Hits);
    }

    [Fact]
    public void Entries_of_deleted_journals_are_pruned()
    {
        Write(_journal, Fixtures.Header, Fixtures.NormalEnd);
        _analyzer.AnalyzeFile(_journal);
        Assert.Equal(0, _analyzer.Cache!.Prune());
        File.Delete(_journal);
        Assert.Equal(1, _analyzer.Cache.Prune());
    }

    [Fact]
    public void Worker_log_lines_are_matched_and_marked_with_their_source()
    {
        Write(_journal, Fixtures.Header, Fixtures.NormalEnd);
        File.WriteAllText(Path.Combine(_dir, "journal.0001.worker1.log"), string.Join("\r\n",
            "'C 05-Nov-2026 09:30:00.000;  ->Worker accepting requests ",
            "' 1:<   System.IO.FileNotFoundException: Could not load file or assembly 'System.Security.Cryptography.Algorithms, Version=6.0.0.0'. ") + "\r\n",
            Encoding.Latin1);

        var r = _analyzer.AnalyzeFile(_journal);

        var finding = r.Findings.Single(f => f.Rule.Id == "observed-cloud-sync-crypto-assembly-missing");
        Assert.Equal("worker1", finding.Evidence[0].Source);
        Assert.Equal("worker1:2", finding.Evidence[0].Where);
        Assert.Equal(2, r.Session.WorkerLogLines);
        Assert.Contains(r.Summary, l => l.Contains("worker log"));
        // The mark survives the cache.
        Assert.Equal("worker1:2", _analyzer.AnalyzeFile(_journal).Findings.Single(f => f.Rule.Id == finding.Rule.Id).Evidence[0].Where);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
