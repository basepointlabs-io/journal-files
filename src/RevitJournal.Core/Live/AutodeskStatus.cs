using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace RevitJournal.Core.Live;

public enum ServiceState { Operational, Maintenance, Degraded, PartialOutage, MajorOutage }

/// <summary>An Autodesk service Revit depends on that is not fully working, and what Autodesk says about it.</summary>
/// <param name="Service">Short name, e.g. "Revit Cloud Worksharing".</param>
/// <param name="Incident">Autodesk's title of the incident, when one is open for this service.</param>
/// <param name="Update">Autodesk's latest update on the incident.</param>
/// <param name="Since">When the incident started (local time); null when Autodesk gives no incident.</param>
public sealed record ServiceProblem(string Service, ServiceState State, string? Incident, string? Update, string Url, DateTime? Since)
{
    public string StateText => State switch
    {
        ServiceState.MajorOutage => "outage",
        ServiceState.PartialOutage => "partial outage",
        ServiceState.Degraded => "degraded performance",
        ServiceState.Maintenance => "maintenance in progress",
        _ => "operational",
    };
}

/// <summary>Maintenance Autodesk has announced for one of the watched services.</summary>
public sealed record ServiceMaintenance(string Title, IReadOnlyList<string> Services, DateTime Start, DateTime? End, string Url);

/// <summary>What the Autodesk Health Dashboard said at <see cref="Checked"/> about the services Revit depends on.</summary>
public sealed record AutodeskStatusReport(DateTime Checked, IReadOnlyList<ServiceProblem> Problems, IReadOnlyList<ServiceMaintenance> Upcoming)
{
    public bool AllOperational => Problems.Count == 0;
}

/// <summary>
/// Reads Autodesk's public status page (health.autodesk.com, the Autodesk Health Dashboard) for the few services a
/// Revit user in the European Union depends on. Only a GET of a public page: nothing about the computer, the user or
/// the models is sent. When the page cannot be reached (no internet, a company firewall) the result is null and the
/// rest of the app works as before.
/// </summary>
public static class AutodeskStatus
{
    public const string PageUrl = "https://health.autodesk.com/";
    public const string SummaryUrl = "https://health.autodesk.com/api/v2/summary.json";
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>The components on the dashboard that matter for Revit in the EU, with the short name the app shows.</summary>
    public static readonly IReadOnlyDictionary<string, string> Watched = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Revit Cloud Worksharing / Cloud Models (European Union)"] = "Revit Cloud Worksharing",
        ["BIM 360 Docs (European Union)"] = "BIM 360 Docs",
        ["BIM 360 Design Collaboration (European Union)"] = "BIM 360 Design Collaboration",
        // Sign-in and licences have one component for all regions.
        ["Identity, Licensing & Entitlement"] = "Sign-in and licensing",
    };

    public static async Task<AutodeskStatusReport?> FetchAsync(HttpClient http, CancellationToken cancel = default)
    {
        try
        {
            string json = await http.GetStringAsync(SummaryUrl, cancel).ConfigureAwait(false);
            return Parse(json, DateTime.Now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads a Statuspage summary.json (components, incidents, scheduled_maintenances).</summary>
    public static AutodeskStatusReport Parse(string json, DateTime checkedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Component id -> short name, for the watched ones.
        var watchedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var states = new List<(string Id, string Service, ServiceState State)>();
        foreach (var c in Array(root, "components"))
        {
            if (Str(c, "name") is not { } name || !Watched.TryGetValue(name, out var service) || Str(c, "id") is not { } id)
                continue;
            watchedIds[id] = service;
            states.Add((id, service, State(Str(c, "status"))));
        }

        var problems = new List<ServiceProblem>();
        var incidents = Array(root, "incidents").ToList();
        foreach (var (id, service, state) in states.Where(s => s.State != ServiceState.Operational))
        {
            // The open incident that names this component, if Autodesk opened one.
            var incident = incidents.FirstOrDefault(i => Array(i, "components").Any(c => Str(c, "id") == id));
            string? update = null;
            if (incident.ValueKind == JsonValueKind.Object)
                update = Array(incident, "incident_updates").Select(u => Str(u, "body")).FirstOrDefault(b => !string.IsNullOrWhiteSpace(b));
            bool found = incident.ValueKind == JsonValueKind.Object;
            problems.Add(new ServiceProblem(service, state,
                found ? Str(incident, "name") : null,
                update is null ? null : Clean(update),
                found ? Str(incident, "shortlink") ?? PageUrl : PageUrl,
                found ? Time(Str(incident, "started_at") ?? Str(incident, "created_at")) : null));
        }

        var upcoming = new List<ServiceMaintenance>();
        foreach (var m in Array(root, "scheduled_maintenances"))
        {
            var services = Array(m, "components").Select(c => Str(c, "id")).OfType<string>()
                .Where(watchedIds.ContainsKey).Select(i => watchedIds[i]).Distinct().ToList();
            if (services.Count == 0 || Time(Str(m, "scheduled_for")) is not { } start)
                continue;
            upcoming.Add(new ServiceMaintenance(Str(m, "name") ?? "Maintenance", services, start, Time(Str(m, "scheduled_until")),
                Str(m, "shortlink") ?? PageUrl));
        }

        return new AutodeskStatusReport(checkedAt,
            [.. problems.OrderByDescending(p => p.State)],
            [.. upcoming.OrderBy(m => m.Start)]);
    }

    /// <summary>The services that started having a problem since <paramref name="before"/>, and those that recovered.</summary>
    public static (IReadOnlyList<ServiceProblem> Started, IReadOnlyList<string> Recovered) Changes(AutodeskStatusReport? before, AutodeskStatusReport now)
    {
        var was = before?.Problems.Select(p => p.Service).ToHashSet(StringComparer.Ordinal) ?? [];
        var started = now.Problems.Where(p => !was.Contains(p.Service)).ToList();
        var recovered = was.Where(s => now.Problems.All(p => p.Service != s)).ToList();
        return (started, recovered);
    }

    /// <summary>"Revit Cloud Worksharing: degraded performance since 10:12".</summary>
    public static string Describe(ServiceProblem p, DateTime now) =>
        $"{p.Service}: {p.StateText}" + (p.Since is { } since ? $" since {When(since, now)}" : "");

    public static string When(DateTime t, DateTime now) =>
        t.Date == now.Date ? t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    private static ServiceState State(string? s) => s switch
    {
        "major_outage" => ServiceState.MajorOutage,
        "partial_outage" => ServiceState.PartialOutage,
        "degraded_performance" => ServiceState.Degraded,
        "under_maintenance" => ServiceState.Maintenance,
        _ => ServiceState.Operational,
    };

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Time(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t.LocalDateTime : null;

    /// <summary>Updates are written for a web page: one line, no markup, not too long for a notification.</summary>
    private static string Clean(string text)
    {
        string noTags = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "");
        string flat = System.Text.RegularExpressions.Regex.Replace(noTags, "\\s+", " ").Trim();
        return flat.Length > 240 ? flat[..237].TrimEnd() + "…" : flat;
    }
}
