using RevitJournal.Core.Parsing;

namespace RevitJournal.Tests;

public class ParserTests
{
    [Theory]
    [InlineData("'C 05-Nov-2026 09:24:38.631;  started", 11)]
    [InlineData("'E 05-Okt-2026 09:24:38.631;   0:<", 10)]
    [InlineData("'H 05-Nov.-2026 09:24:38.631;   0:<", 11)]
    [InlineData("'C 14-juil.-2026 09:24:38.631;  x", 7)]
    [InlineData("'C 03-sept.-2026 09:24:38.631;  x", 9)]
    [InlineData("'C 05-12-2026 09:24:38.631;  x", 12)]
    public void Parses_localized_timestamps(string line, int month)
    {
        Assert.True(JournalTimestamp.TryParse(line, out var t));
        Assert.Equal(month, t.Month);
        Assert.Equal(new TimeSpan(0, 9, 24, 38, 631), t.TimeOfDay);
    }

    [Fact]
    public void Ignores_lines_without_block_timestamp() =>
        Assert.False(JournalTimestamp.TryParse("' 0:< Time: 11.5.2026 11:30:38:454 Calling: BeginWriteData", out _));

    [Fact]
    public void Reads_header_and_memory()
    {
        var s = JournalParser.Parse("journal.0001.txt", Fixtures.Lines(Fixtures.Header, Fixtures.FailedCloudSync));

        Assert.Equal("2025.4.2", s.Release);
        Assert.Equal(new DateOnly(2025, 5, 15), s.BuildDate);
        Assert.Equal(32212, s.TotalRamMb);
        Assert.Contains(s.Ram, r => r.AvailableMb == 14789);
        Assert.False(s.EndedNormally);
    }

    [Fact]
    public void Failed_sync_collects_exception_and_dialog()
    {
        var s = JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, Fixtures.FailedCloudSync, Fixtures.GoodSync));

        Assert.Equal(2, s.Syncs.Count);
        var failed = s.Syncs[0];
        Assert.True(failed.Finished);
        Assert.False(failed.Succeeded);
        Assert.Equal(2, failed.Errors.Count); // the exception line and the error dialog
        Assert.Equal(1, failed.ReloadLatestCount);
        Assert.True(s.Syncs[1].Succeeded);
        Assert.Equal(TimeSpan.FromMilliseconds(62710), s.Syncs[1].Duration);
        Assert.Equal("ID_FILE_SAVE_TO_CENTRAL", s.UserActions.Single().CommandId);
        Assert.Equal("TaskDialog_Operation_Could_Not_Be_Completed", s.Dialogs.Single().Id);
    }

    [Fact]
    public void Detects_exit_request_and_normal_end()
    {
        var exitOnly = JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, Fixtures.ExitRequested));
        Assert.NotNull(exitOnly.ExitRequestedAt);
        Assert.False(exitOnly.EndedNormally);

        var normal = JournalParser.Parse("j", Fixtures.Lines(Fixtures.Header, Fixtures.ExitRequested, Fixtures.NormalEnd));
        Assert.True(normal.EndedNormally);
    }
}
