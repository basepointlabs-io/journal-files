using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;

namespace RevitJournal.Tests;

/// <summary>This computer against Autodesk's published figures (system requirements 2025/2026, cloud model disk space, page file).</summary>
public class ComputerRequirementsTests
{
    private static SystemSample Computer(long ramMb = 49_028, long commitMb = 77_000, long freeDiskMb = 107_000, string version = "2025",
        long? videoMb = 8_192, bool? accelerator = true) =>
        new(new DateTime(2026, 11, 18, 10, 0, 0), ramMb, 15_000, commitMb, 9_000, 0, 0, 8,
            [new RevitProcessSample(100, version, new DateTime(2026, 11, 18, 8, 0, 0), true, 30_000, 0, 1_500, 800)],
            [new DiskSample("C:", freeDiskMb, 500_000)], videoMb, accelerator, "C:");

    private static ModelLoad Cloud(int modelMb, int linksMb, int links = 10, int known = 10) =>
        new("Tower.rvt", modelMb, null, links, known, linksMb, "", false, IsCloud: true);

    private static RequirementCheck Row(IReadOnlyList<RequirementCheck> rows, string item) => rows.Single(r => r.Item == item);

    [Fact]
    public void RAM_is_judged_by_Autodesks_table_for_the_largest_open_model()
    {
        // 48 GB against a 700 MB model: Autodesk's table asks 64 GB (Performance, up to ~1 GB).
        var ram = Row(ComputerRequirements.Check(Computer(), [Cloud(700, 0, 0, 0)]), "RAM");
        Assert.Equal(HealthLevel.Watch, ram.Level);
        Assert.StartsWith("64.0 GB (Performance) for Tower.rvt", ram.Autodesk);
        Assert.Contains("short of Autodesk's figure", ram.Note);
        Assert.Equal(AutodeskArticle.SystemRequirements, ram.Source);

        // 500 MB fits 32 GB (Value); a 64 GB machine that Windows reports as 63.7 GB counts as 64.
        Assert.Equal(HealthLevel.Ok, Row(ComputerRequirements.Check(Computer(), [Cloud(500, 0, 0, 0)]), "RAM").Level);
        Assert.Equal(HealthLevel.Ok, Row(ComputerRequirements.Check(Computer(ramMb: 65_200), [Cloud(900, 0, 0, 0)]), "RAM").Level);

        // Above Autodesk's largest configuration the row says Autodesk has no figure.
        var beyond = Row(ComputerRequirements.Check(Computer(ramMb: 131_000), [Cloud(2_000, 0, 0, 0)]), "RAM");
        Assert.Contains("larger than the ~1 GB", beyond.Autodesk);
        Assert.Equal("Autodesk gives no figure for models this large", beyond.Note);
    }

    [Fact]
    public void Cloud_models_need_20_times_the_model_and_3_times_the_links_on_the_cache_drive()
    {
        // 600 MB model and 8 GB of links: 12,000 + 24,576 MB = 35.7 GB, against 30 GB free.
        var rows = ComputerRequirements.Check(Computer(freeDiskMb: 30_720), [Cloud(600, 8_192)]);
        var cloud = Row(rows, "Free disk C: for cloud models");
        Assert.Equal(HealthLevel.Watch, cloud.Level);
        Assert.StartsWith("35.7 GB for Tower.rvt", cloud.Autodesk);
        Assert.Equal(AutodeskArticle.CloudModelCorrupt, cloud.Source);

        // Links that are not on this computer are not counted, and the row says so.
        var partly = Row(ComputerRequirements.Check(Computer(), [Cloud(600, 1_000, links: 12, known: 3)]), "Free disk C: for cloud models");
        Assert.Contains("links not found on this computer are not counted", partly.Note);
    }

    [Fact]
    public void Disk_page_file_video_memory_and_accelerator_follow_Autodesks_figures()
    {
        var rows = ComputerRequirements.Check(Computer(version: "2026", freeDiskMb: 60_000, commitMb: 160_000, videoMb: 2_048, accelerator: false),
            [Cloud(300, 100)]);

        var disk = Row(rows, "Free disk C:");
        Assert.Equal(HealthLevel.Ok, disk.Level);                                     // 30 GB required
        Assert.Equal("below the 100 GB Autodesk recommends", disk.Note);             // 2026 recommends 100 GB
        Assert.Equal(AutodeskArticle.SystemRequirements2026, disk.Source);

        var page = Row(rows, "Page file");                                            // 110 GB > 2 x 48 GB
        Assert.Equal(HealthLevel.Watch, page.Level);
        Assert.Equal(AutodeskArticle.OptimizeOs, page.Source);

        Assert.Equal(HealthLevel.Watch, Row(rows, "Video memory").Level);
        Assert.Equal(HealthLevel.Watch, Row(rows, "Revit Personal Accelerator").Level);

        // Without a cloud model the accelerator is not asked for.
        Assert.DoesNotContain(ComputerRequirements.Check(Computer(accelerator: false), []), r => r.Item == "Revit Personal Accelerator");
    }

    [Fact]
    public void Too_little_space_for_the_cloud_model_becomes_an_issue_with_Autodesks_advice()
    {
        var probe = new FixedProbe(Computer(freeDiskMb: 40_000));
        var r = new HealthMonitor(probe).Tick([Cloud(1_000, 10_000)]);

        var issue = r.Issues.Single(i => i.Key == "disk-cloud");
        Assert.Equal(HealthLevel.Watch, issue.Level);
        Assert.Contains("Autodesk asks for 48.8 GB", issue.Title);
        Assert.Contains("[Autodesk: Data in File Is Corrupt When Opening Cloud Model]", issue.Advice);
        Assert.NotNull(r.Requirements);
    }

    private sealed class FixedProbe(SystemSample sample) : ISystemProbe
    {
        public SystemSample Sample() => sample;
    }
}
