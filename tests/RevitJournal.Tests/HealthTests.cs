using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;

namespace RevitJournal.Tests;

public class HealthTests
{
    private sealed class FakeProbe : ISystemProbe
    {
        public DateTime Time = new(2026, 11, 9, 10, 0, 0);
        public long TotalRam = 32_000, FreeRam = 16_000, CommitLimit = 40_000, CommitFree = 20_000;
        public double Idle, Total;
        public List<RevitProcessSample> Revits = [new(100, "2025", new DateTime(2026, 11, 9, 9, 0, 0), true, 6_000, 0, 1_500, 800)];
        public List<DiskSample> Disks = [new("C:", 120_000, 500_000)];

        public SystemSample Sample() => new(Time, TotalRam, FreeRam, CommitLimit, CommitFree, Idle, Total, 8, [.. Revits], [.. Disks]);

        /// <summary>Moves the clock; the processor is half busy meanwhile.</summary>
        public void Advance(int seconds)
        {
            Time = Time.AddSeconds(seconds);
            Total += seconds * 8;
            Idle += seconds * 4;
        }
    }

    [Fact]
    public void A_healthy_computer_has_no_issues_and_shows_its_numbers()
    {
        var probe = new FakeProbe();
        var monitor = new HealthMonitor(probe);
        monitor.Tick();
        probe.Advance(5);
        probe.Revits[0] = probe.Revits[0] with { CpuSeconds = 10 };   // 10 s of CPU in 5 s on 8 cores = 25 %
        var r = monitor.Tick();

        Assert.Equal(HealthLevel.Ok, r.Level);
        Assert.Empty(r.Issues);
        Assert.Empty(monitor.NewAlerts);
        Assert.Equal(50, r.CpuPercent, 1);
        Assert.Equal(25, r.Revits[0].CpuPercent, 1);
        Assert.Equal(1_500, r.Revits[0].GdiObjects);
        Assert.Null(r.RamRunsOutIn);
    }

    [Fact]
    public void Falling_memory_is_only_forecast_once_memory_is_short_and_notifies_only_when_minutes_are_left()
    {
        var probe = new FakeProbe();
        var monitor = new HealthMonitor(probe);
        HealthReport r = monitor.Tick();
        var alerts = new List<LiveAlert>();
        HealthReport? firstForecast = null;
        // 20 MB less every 5 s = 240 MB per minute, from 16 GB of 32 GB free.
        for (int i = 0; i < 12 * 60 && alerts.Count == 0; i++)
        {
            probe.Advance(5);
            probe.FreeRam -= 20;
            r = monitor.Tick();
            alerts.AddRange(monitor.NewAlerts);
            if (r.Issues.Any(x => x.Key == "ram-forecast"))
                firstForecast ??= r;
            else if (firstForecast is null)
                Assert.Null(r.RamRunsOutIn);                              // plenty of memory: no forecast, however fast it falls
        }

        // Shown in the panel once less than a quarter of the memory is free, without a notification...
        Assert.NotNull(firstForecast);
        Assert.True(firstForecast.FreeRamMb < 0.25 * 32_000);
        Assert.Equal(HealthLevel.Watch, firstForecast.Issues.Single(x => x.Key == "ram-forecast").Level);
        // ...and one notification when about ten minutes are left.
        Assert.InRange(r.RamRunsOutIn!.Value.TotalMinutes, 8, 10.1);
        Assert.Contains("still falling", Assert.Single(alerts).Title);

        // Memory that jitters without a trend gives no forecast.
        var calm = new FakeProbe();
        var calmMonitor = new HealthMonitor(calm);
        for (int i = 0; i < 100; i++)
        {
            calm.Advance(5);
            calm.FreeRam = 16_000 + (i % 2 == 0 ? 150 : -150);
            Assert.Null(calmMonitor.Tick().RamRunsOutIn);
        }
    }

    [Fact]
    public void Models_too_large_together_are_a_quiet_issue_with_Autodesks_advice()
    {
        var monitor = new HealthMonitor(new FakeProbe());   // 32 GB
        ModelLoad[] models = [new("A.rvt", 1_000, null, 0, 0, 0, "about 19.5 GB of RAM", false), new("B.rvt", 800, null, 0, 0, 0, "about 15.6 GB of RAM", false)];

        var r = monitor.Tick(models);

        var issue = r.Issues.Single(i => i.Key == "models-large");
        Assert.Equal(HealthLevel.Watch, issue.Level);
        Assert.Contains("2 open models are 1.8 GB on disk together", issue.Title);
        Assert.Contains(": about 35.2 GB of RAM; this computer has", issue.Advice);
        Assert.EndsWith("[Autodesk: Memory Usage and Instability]", issue.Advice);
        Assert.Empty(monitor.NewAlerts);                                            // shown in the panel, no notification
        Assert.DoesNotContain(monitor.Tick([models[0]]).Issues, i => i.Key == "models-large");
    }

    [Fact]
    public void Limits_raise_issues_and_serious_ones_notify_once_per_half_hour()
    {
        var probe = new FakeProbe { FreeRam = 900 };
        probe.Revits[0] = probe.Revits[0] with { GdiObjects = 9_300, Responding = false };
        probe.Revits.Add(new RevitProcessSample(200, "2026", new DateTime(2026, 11, 9, 9, 30, 0), true, 18_000, 0, 7_200, 500));
        probe.Disks[0] = new DiskSample("C:", 800, 500_000);
        var monitor = new HealthMonitor(probe);

        var r = monitor.Tick();

        Assert.Equal(HealthLevel.Danger, r.Level);
        Assert.Equal(HealthLevel.Danger, r.Issues.Single(i => i.Key == "ram-low").Level);
        Assert.Equal(HealthLevel.Danger, r.Issues.Single(i => i.Key == "gdi:100").Level);
        Assert.Contains("9,300 of 10,000", r.Issues.Single(i => i.Key == "gdi:100").Title);
        Assert.Equal(HealthLevel.Watch, r.Issues.Single(i => i.Key == "gdi:200").Level);
        Assert.Equal(HealthLevel.Watch, r.Issues.Single(i => i.Key == "hung:100").Level);
        Assert.Contains("not using the processor", r.Issues.Single(i => i.Key == "hung:100").Title);   // stuck, not busy
        Assert.Equal(HealthLevel.Danger, r.Issues.Single(i => i.Key == "disk:C:").Level);
        Assert.Contains(r.Issues, i => i.Key == "many-revits");          // 24 of 32 GB in two Revit windows
        Assert.Equal(HealthLevel.Danger, r.Issues[0].Level);             // most serious first
        Assert.Equal(HealthLevel.Danger, r.Revits[0].Level);

        // Three serious problems: three notifications now, none again for half an hour, then again.
        Assert.Equal(3, monitor.NewAlerts.Count);
        Assert.Equal([LiveAlertKind.LowMemory, LiveAlertKind.DiskLow, LiveAlertKind.ResourcesNearLimit], monitor.NewAlerts.Select(a => a.Kind).Order());
        probe.Advance(60);
        probe.Revits[0] = probe.Revits[0] with { CpuSeconds = 120 };   // 120 s of CPU in 60 s on 8 cores = 25 %: it is working
        Assert.Contains("is busy", monitor.Tick().Issues.Single(i => i.Key == "hung:100").Title);
        Assert.Empty(monitor.NewAlerts);
        probe.Advance(31 * 60);
        monitor.Tick();
        Assert.Equal(3, monitor.NewAlerts.Count);
    }
}
