using System.Text.Json;
using System.Text.Json.Serialization;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Symptoms;

/// <summary>A start-screen symptom from knowledge-base/symptoms.json.</summary>
public sealed class Symptom
{
    public required string Id { get; init; }
    public string Icon { get; init; } = "";
    public required LocalizedText Title { get; init; }
    public required LocalizedText Subtitle { get; init; }

    /// <summary>Which journal to diagnose: "last-abnormal-end", "last-sync" or "running-or-latest".</summary>
    public required string Journal { get; init; }

    public List<string> Categories { get; init; } = [];
    public List<string> Exclude { get; init; } = [];
    public required LocalizedSteps Checklist { get; init; }

    public bool IsRelevant(Rule rule) => Categories.Contains(rule.Category) && !Exclude.Contains(rule.Id);
}

public static class SymptomCatalog
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<Symptom> LoadEmbedded()
    {
        using var stream = typeof(SymptomCatalog).Assembly.GetManifestResourceStream("symptoms.json")
                           ?? throw new InvalidDataException("symptoms.json is not embedded");
        return JsonSerializer.Deserialize<SymptomFile>(stream, Options)?.Symptoms
               ?? throw new InvalidDataException("symptoms.json is empty");
    }

    private sealed class SymptomFile
    {
        [JsonPropertyName("symptoms")] public List<Symptom> Symptoms { get; init; } = [];
    }
}
