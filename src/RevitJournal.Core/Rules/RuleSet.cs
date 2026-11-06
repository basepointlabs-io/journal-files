using System.Reflection;
using System.Text.Json;

namespace RevitJournal.Core.Rules;

/// <summary>The loaded rule base: by default the copy embedded in this assembly.</summary>
public sealed class RuleSet
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Dictionary<string, Rule> _byId;

    public IReadOnlyList<Rule> Rules { get; }

    private RuleSet(List<Rule> rules)
    {
        Rules = rules;
        _byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
    }

    public Rule? this[string id] => _byId.GetValueOrDefault(id);

    public static RuleSet LoadEmbedded()
    {
        var asm = typeof(RuleSet).Assembly;
        var rules = new List<Rule>();
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("rules/", StringComparison.Ordinal)).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            rules.AddRange(Read(stream, name["rules/".Length..]));
        }
        return new RuleSet(rules);
    }

    public static RuleSet LoadFromDirectory(string directory)
    {
        var rules = new List<Rule>();
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Order())
        {
            using var stream = File.OpenRead(file);
            rules.AddRange(Read(stream, Path.GetFileName(file)));
        }
        return new RuleSet(rules);
    }

    private static IEnumerable<Rule> Read(Stream stream, string origin)
    {
        var file = JsonSerializer.Deserialize<RuleFile>(stream, Options)
                   ?? throw new InvalidDataException($"Empty rule file {origin}");
        foreach (var rule in file.Rules)
        {
            rule.Origin = origin;
            yield return rule;
        }
    }

    private sealed class RuleFile
    {
        public string Source { get; init; } = "";
        public List<Rule> Rules { get; init; } = [];
    }
}
