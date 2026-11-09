using System.Text;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;
using RevitJournal.Core.Parsing;

namespace RevitJournal.Tests;

public sealed class LiveTests : IDisposable
{
    private static readonly Analyzer Analyzer = Analyzer.CreateDefault();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rja-live-" + Guid.NewGuid().ToString("N"));
    private readonly string _journal;
    private FileStream? _writer;

    public LiveTests()
    {
        string folder = Path.Combine(_root, "Autodesk Revit 2025", "Journals");
        Directory.CreateDirectory(folder);
        _journal = Path.Combine(folder, "journal.0001.txt");
    }

    /// <summary>Plays Revit: keeps the journal open for writing and appends raw text (possibly mid-line).</summary>
    private void Write(string text)
    {
        _writer ??= new FileStream(_journal, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        byte[] bytes = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        _writer.Write(bytes);
        _writer.Flush();
    }

    private void CloseRevit()
    {
        _writer?.Dispose();
        _writer = null;
    }

    [Fact]
    public void Reading_in_pieces_gives_the_same_result_as_reading_the_whole_file()
    {
        string text = string.Join("\n", Fixtures.Lines(Fixtures.Header, Fixtures.FailedCloudSync, Fixtures.GoodSync, Fixtures.ExitRequested)) + "\n";
        var full = JournalParser.Parse("j", text.TrimEnd('\n').Split('\n'));

        Write("");
        using var live = new LiveJournal(_journal, Analyzer.Matcher);
        // Cut in awkward places: inside a line, inside a "Jrn.Command" statement, between CR and LF.
        int written = 0;
        foreach (int cut in new[] { 7, 120, 333, 601, 900, text.Length }.Where(c => c <= text.Length))
        {
            Write(text[written..cut]);
            written = cut;
            live.Poll();
        }

        var s = live.Session;
        Assert.Equal(full.LineCount, s.LineCount);
        Assert.Equal(full.Syncs.Count, s.Syncs.Count);
        Assert.Equal(full.Syncs.Count(x => x.Succeeded), s.Syncs.Count(x => x.Succeeded));
        Assert.Equal(full.UserActions.Select(a => a.CommandId), s.UserActions.Select(a => a.CommandId));
        Assert.Equal(full.ExitRequestedAt, s.ExitRequestedAt);
        Assert.Contains("observed-cloud-sync-crypto-assembly-missing", s.RuleHits!.Keys);
    }

    [Fact]
    public void Monitor_alerts_on_failed_sync_not_responding_and_crash_but_not_on_old_events()
    {
        var now = new DateTime(2026, 11, 5, 11, 30, 0);
        var procs = new FakeProcesses();
        using var monitor = new LiveMonitor(Analyzer, _root, procs, () => now, () => []);

        // Revit already had a failed sync before the monitor started: that is history, not news.
        Write(string.Join("\n", Fixtures.Lines(Fixtures.Header, Fixtures.FailedCloudSync)) + "\n");
        Assert.Empty(monitor.Tick());
        Assert.Single(monitor.Status);

        // A new failed sync while we watch.
        Write("""
              ' 1:< SLOG $acbbc009 2026-11-05 11:40:00.000 >STC
              ' 1:<   System.IO.FileNotFoundException: Could not load file or assembly 'X'.
              ' 1:< SLOG $acbbc009 2026-11-05 11:40:01.000 <STC

            """);
        now = now.AddMinutes(10);
        var alerts = monitor.Tick();
        Assert.Equal(LiveAlertKind.SyncFailed, Assert.Single(alerts).Kind);
        Assert.Equal("wont-sync", alerts[0].SymptomId);

        // Windows says Revit is not responding; we wait a minute before telling the user, and tell only once.
        procs.Responding = false;
        Assert.Empty(monitor.Tick());
        now = now.AddSeconds(70);
        Assert.Equal(LiveAlertKind.NotResponding, Assert.Single(monitor.Tick()).Kind);
        now = now.AddSeconds(70);
        Assert.Empty(monitor.Tick());

        // Revit disappears without writing its shutdown lines.
        // The monitor waits a few seconds for Windows to record a crash before it judges the ending.
        CloseRevit();
        Assert.Empty(monitor.Tick());
        Assert.Empty(monitor.Status);
        now = now.AddSeconds(15);
        var crash = Assert.Single(monitor.Tick());
        Assert.Equal(LiveAlertKind.Crashed, crash.Kind);
        Assert.Equal("Revit 2025 closed unexpectedly", crash.Title);   // no proof of a crash was given
        // The final analysis is handed over once, for the window to show without reading the file again.
        Assert.Equal(SessionStatus.EndedAbnormally, monitor.Results[_journal].Status);
        Assert.Empty(monitor.Tick());
        Assert.Empty(monitor.Results);
    }

    [Fact]
    public void Live_results_follow_the_journal_and_use_frozen_copies()
    {
        var now = new DateTime(2026, 11, 5, 11, 30, 0);
        using var monitor = new LiveMonitor(Analyzer, _root, new FakeProcesses(), () => now, () => []);
        Write(string.Join("\n", Fixtures.Lines(Fixtures.Header)) + "\n");
        monitor.Tick();
        var first = monitor.Results[_journal];
        Assert.Equal(SessionStatus.Running, first.Status);
        monitor.Tick();
        Assert.Same(first, monitor.Results[_journal]);                 // nothing new: not analyzed again

        Write(string.Join("\n", Fixtures.Lines(Fixtures.GoodSync)) + "\n");
        monitor.Tick();
        var second = monitor.Results[_journal];
        Assert.NotSame(first, second);
        Assert.Single(second.Session.Syncs);
        Assert.Empty(first.Session.Syncs);                             // the earlier result did not change under the reader
        Assert.True(second.Session.Tail.Count <= RevitJournal.Core.Model.JournalSession.TailSize);
    }

    [Fact]
    public void Not_responding_goes_to_the_journal_of_the_process_that_started_with_it()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        var procs = new TwoProcesses();
        using var monitor = new LiveMonitor(Analyzer, _root, procs, () => now, () => []);

        // Two Revit 2025 windows: one started at 09:24:38 (Fixtures.Header), the other at 11:00:00.
        Write(string.Join("\n", Fixtures.Lines(Fixtures.Header)) + "\n");
        string second = Path.Combine(Path.GetDirectoryName(_journal)!, "journal.0002.txt");
        using var other = new FileStream(second, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        other.Write(Encoding.Latin1.GetBytes(string.Join("\r\n", Fixtures.Lines(Fixtures.Header.Replace("09:24:38.631", "11:00:00.000"))) + "\r\n"));
        other.Flush();
        monitor.Tick();

        procs.LateOneResponding = false;                               // the window started at 11:00 hangs
        monitor.Tick();
        now = now.AddSeconds(70);
        var alert = Assert.Single(monitor.Tick());
        Assert.Equal(LiveAlertKind.NotResponding, alert.Kind);
        Assert.Equal(second, alert.JournalPath);
    }

    [Fact]
    public void Closing_revit_normally_raises_nothing()
    {
        var now = new DateTime(2026, 11, 5, 16, 0, 0);
        using var monitor = new LiveMonitor(Analyzer, _root, new FakeProcesses(), () => now, () => []);
        Write(string.Join("\n", Fixtures.Lines(Fixtures.Header)) + "\n");
        monitor.Tick();
        Write(string.Join("\n", Fixtures.Lines(Fixtures.ExitRequested, Fixtures.NormalEnd)) + "\n");
        CloseRevit();
        Assert.Empty(monitor.Tick());
    }

    public void Dispose()
    {
        CloseRevit();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class FakeProcesses : IRevitProcesses
    {
        public bool Responding { get; set; } = true;
        public IReadOnlyList<RevitProcess> List() => [new RevitProcess(1, "2025", Responding, new DateTime(2026, 11, 5, 9, 24, 30))];
    }

    private sealed class TwoProcesses : IRevitProcesses
    {
        public bool LateOneResponding { get; set; } = true;
        public IReadOnlyList<RevitProcess> List() =>
        [
            new RevitProcess(1, "2025", true, new DateTime(2026, 11, 5, 9, 24, 30)),
            new RevitProcess(2, "2025", LateOneResponding, new DateTime(2026, 11, 5, 10, 59, 52)),
        ];
    }
}
