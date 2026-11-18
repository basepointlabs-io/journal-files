using RevitJournal.Core;
using RevitJournal.Core.Analysis;

namespace RevitJournal.Tests;

/// <summary>After a crash Revit's error report leaves "journal.NNNN.txt.copy.txt", and often the journal itself is gone.</summary>
public class JournalLocatorTests
{
    [Fact]
    public void A_crash_copy_stands_in_for_a_journal_that_is_gone_and_finds_its_dump()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rd-journals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            foreach (string name in new[]
                     {
                         "journal.0668.txt",                                  // an ordinary session
                         "journal.0669.txt.copy.txt", "journal.0669.0001.dmp", "journal.0669.txt.copy.txt.abbrev", // crashed, journal gone
                         "journal.0670.txt", "journal.0670.txt.copy.txt",    // both: the journal itself is read
                     })
                File.WriteAllText(Path.Combine(folder, name), "'C 30-Oct-2026 09:56:14.643;  started recording journal file\n");

            var found = JournalLocator.FindInFolder(folder).Select(j => Path.GetFileName(j.Path)).Order().ToList();
            Assert.Equal(["journal.0668.txt", "journal.0669.txt.copy.txt", "journal.0670.txt"], found);

            var copy = Path.Combine(folder, "journal.0669.txt.copy.txt");
            Assert.Equal("journal.0669", JournalLocator.Stem(copy));
            Assert.Equal(["journal.0669.0001.dmp"], AnalysisContext.ForFile(copy).DumpFiles.Select(Path.GetFileName));
            Assert.Equal("journal.0669.txt.copy.txt", AnalysisContext.ForFile(Path.Combine(folder, "journal.0670.txt")).Previous?.Name);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
