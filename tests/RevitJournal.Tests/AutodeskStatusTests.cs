using RevitJournal.Core.Live;
using Xunit;

namespace RevitJournal.Tests;

/// <summary>The Autodesk Health Dashboard summary, cut down to its shape; names and ids are made up except the component names the app watches.</summary>
public class AutodeskStatusTests
{
    private const string Summary = """
    {
      "page": { "name": "Autodesk Health Dashboard" },
      "status": { "indicator": "minor", "description": "Partially Degraded Service" },
      "components": [
        { "id": "c-rcw-eu", "name": "Revit Cloud Worksharing / Cloud Models (European Union)", "status": "degraded_performance" },
        { "id": "c-rcw-us", "name": "Revit Cloud Worksharing / Cloud Models", "status": "major_outage" },
        { "id": "c-docs-eu", "name": "BIM 360 Docs (European Union)", "status": "operational" },
        { "id": "c-id", "name": "Identity, Licensing & Entitlement", "status": "operational" },
        { "id": "c-dc-eu", "name": "BIM 360 Design Collaboration (European Union)", "status": "under_maintenance" }
      ],
      "incidents": [
        {
          "name": "Slow synchronization of cloud models",
          "status": "investigating",
          "shortlink": "https://stspg.io/example",
          "started_at": "2026-11-11T09:12:00.000Z",
          "components": [ { "id": "c-rcw-eu" } ],
          "incident_updates": [
            { "status": "investigating", "body": "We are investigating <b>slow</b> syncs\nfor cloud models.", "created_at": "2026-11-11T09:20:00.000Z" }
          ]
        }
      ],
      "scheduled_maintenances": [
        {
          "name": "Planned database upgrade",
          "status": "scheduled",
          "shortlink": "https://stspg.io/maint",
          "scheduled_for": "2026-11-12T01:00:00.000Z",
          "scheduled_until": "2026-11-12T03:00:00.000Z",
          "components": [ { "id": "c-docs-eu" } ]
        },
        {
          "name": "Somewhere else",
          "scheduled_for": "2026-11-12T01:00:00.000Z",
          "components": [ { "id": "c-rcw-us" } ]
        }
      ]
    }
    """;

    [Fact]
    public void Reads_only_the_watched_EU_services()
    {
        var r = AutodeskStatus.Parse(Summary, new DateTime(2026, 11, 11, 10, 35, 0));

        // The US outage is not ours; the EU worksharing and the EU design collaboration are.
        Assert.Equal(["Revit Cloud Worksharing", "BIM 360 Design Collaboration"], r.Problems.Select(p => p.Service));
        var rcw = r.Problems[0];
        Assert.Equal(ServiceState.Degraded, rcw.State);
        Assert.Equal("Slow synchronization of cloud models", rcw.Incident);
        Assert.Equal("We are investigating slow syncs for cloud models.", rcw.Update);
        Assert.Equal("https://stspg.io/example", rcw.Url);
        Assert.Equal(new DateTimeOffset(2026, 11, 11, 9, 12, 0, TimeSpan.Zero).LocalDateTime, rcw.Since);

        // Maintenance without an incident still counts, and links to the dashboard.
        Assert.Equal(ServiceState.Maintenance, r.Problems[1].State);
        Assert.Null(r.Problems[1].Incident);
        Assert.Equal(AutodeskStatus.PageUrl, r.Problems[1].Url);

        var m = Assert.Single(r.Upcoming);
        Assert.Equal("Planned database upgrade", m.Title);
        Assert.Equal(["BIM 360 Docs"], m.Services);
    }

    [Fact]
    public void Says_what_started_and_what_recovered()
    {
        var at = new DateTime(2026, 11, 11, 10, 0, 0);
        var before = new AutodeskStatusReport(at, [new ServiceProblem("BIM 360 Docs", ServiceState.Degraded, null, null, AutodeskStatus.PageUrl, null)], []);
        var now = AutodeskStatus.Parse(Summary, at.AddMinutes(5));

        var (started, recovered) = AutodeskStatus.Changes(before, now);

        Assert.Equal(["Revit Cloud Worksharing", "BIM 360 Design Collaboration"], started.Select(p => p.Service));
        Assert.Equal(["BIM 360 Docs"], recovered);
        Assert.Empty(AutodeskStatus.Changes(now, now).Started);
    }

    [Fact]
    public void Describes_a_problem_in_one_line()
    {
        var since = new DateTime(2026, 11, 11, 10, 12, 0);
        var p = new ServiceProblem("Revit Cloud Worksharing", ServiceState.Degraded, null, null, AutodeskStatus.PageUrl, since);
        Assert.Equal("Revit Cloud Worksharing: degraded performance since 10:12", AutodeskStatus.Describe(p, since.AddMinutes(20)));
        Assert.Equal("Revit Cloud Worksharing: degraded performance since 11 Nov 10:12", AutodeskStatus.Describe(p, since.AddDays(1)));
    }

    [Fact]
    public void An_unexpected_page_gives_no_problems()
    {
        var r = AutodeskStatus.Parse("""{ "page": {} }""", DateTime.Now);
        Assert.True(r.AllOperational);
        Assert.Empty(r.Upcoming);
    }
}
