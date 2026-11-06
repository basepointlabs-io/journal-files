using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitJournal.Core.Rules;

/// <summary>One entry of the rule base. Mirrors knowledge-base/schema.json.</summary>
public sealed class Rule
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required Severity Severity { get; init; }
    public RuleMatch Match { get; init; } = new();
    public string? Context { get; init; }
    public string? Versions { get; init; }
    public required LocalizedText Title { get; init; }
    public required LocalizedText Cause { get; init; }
    public required LocalizedSteps Fix { get; init; }
    public List<RuleSource> Sources { get; init; } = [];
    public required Confidence Confidence { get; init; }
    public List<string> Related { get; init; } = [];
    public JsonElement? Metric { get; init; }

    /// <summary>File the rule came from, e.g. "autodesk-help.json".</summary>
    [JsonIgnore] public string Origin { get; internal set; } = "";

    public bool IsTextRule => Match.Any.Count > 0 || Match.Regex.Count > 0;
}

public sealed class RuleMatch
{
    public List<string> Any { get; init; } = [];
    public List<string> Regex { get; init; } = [];
    [JsonPropertyName("ignore_case")] public bool IgnoreCase { get; init; }
    public List<string> Exclude { get; init; } = [];
}

public sealed class LocalizedText
{
    public string En { get; init; } = "";
    public string? Uk { get; init; }
}

public sealed class LocalizedSteps
{
    public List<string> En { get; init; } = [];
    public List<string>? Uk { get; init; }
}

public sealed record RuleSource(string Title, string Url);

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity { Info, Low, Medium, High, Critical }

[JsonConverter(typeof(KebabEnumConverter<Confidence>))]
public enum Confidence { Community, CommunityVerified, Official }

/// <summary>Reads enum values written in kebab-case ("community-verified").</summary>
internal sealed class KebabEnumConverter<T>() : JsonStringEnumConverter<T>(JsonNamingPolicy.KebabCaseLower) where T : struct, Enum;
