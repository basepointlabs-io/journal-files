using System.Text.RegularExpressions;
using RevitJournal.Core.Model;
using RevitJournal.Core.Parsing;

namespace RevitJournal.Core.Rules;

/// <summary>Matches every text rule against every physical journal line.</summary>
public sealed class RuleMatcher
{
    private sealed record Compiled(Rule Rule, string[] Substrings, Regex[] Regexes, string[] Excludes, StringComparison Comparison, HashSet<string> Dialogs);

    /// <summary>How Revit logs the id of a dialog, on the line under its text.</summary>
    private const string DialogIdPrefix = "'Id : ";

    /// <summary>The id is on the line after the text; a little room for a text that Revit wraps over lines.</summary>
    private const int DialogIdDistance = 3;

    private readonly Compiled[] _rules;

    public RuleMatcher(RuleSet ruleSet)
    {
        _rules = ruleSet.Rules.Where(r => r.IsTextRule).Select(r =>
        {
            var options = RegexOptions.CultureInvariant | (r.Match.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            return new Compiled(
                r,
                [.. r.Match.Any, .. r.Match.Translations],
                [.. r.Match.Regex.Select(p => new Regex(p, options | RegexOptions.Compiled))],
                [.. r.Match.Exclude],
                r.Match.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal,
                new HashSet<string>(r.Match.Dialogs, StringComparer.Ordinal));
        }).ToArray();
    }

    /// <summary>Rule id → matching lines, in file order.</summary>
    public Dictionary<string, List<LineRef>> Match(JournalSession session)
    {
        var hits = new Dictionary<string, List<LineRef>>(StringComparer.Ordinal);
        DateTime? ts = null;
        var lines = session.Lines;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (JournalTimestamp.TryParse(line, out var t))
                ts = t;
            MatchLine(line, i + 1, ts, hits);
        }
        return hits;
    }

    /// <summary>Adds the rules that match one line (1-based <paramref name="lineNumber"/>) to <paramref name="hits"/>.</summary>
    public void MatchLine(string line, int lineNumber, DateTime? time, Dictionary<string, List<LineRef>> hits, string? source = null)
    {
        // "'Id : TaskDialog_Revit_File_Corrupted": the dialog's id, the same in every language.
        string? dialog = null;
        var trimmed = line.AsSpan().Trim();
        if (trimmed.StartsWith(DialogIdPrefix, StringComparison.Ordinal))
            dialog = trimmed[DialogIdPrefix.Length..].ToString();

        foreach (var c in _rules)
        {
            bool byDialog = dialog is not null && c.Dialogs.Contains(dialog);
            if (!byDialog && !IsHit(c, line))
                continue;
            if (!hits.TryGetValue(c.Rule.Id, out var list))
                hits[c.Rule.Id] = list = [];
            // The id follows the dialog's text: when the rule already fired on the text just above, it is the same dialog.
            if (byDialog && list.Count > 0 && list[^1].Source == source && lineNumber - list[^1].Line is > 0 and <= DialogIdDistance)
                continue;
            list.Add(new LineRef(lineNumber, time, line.Trim(), source));
        }
    }

    private static bool IsHit(Compiled c, string line)
    {
        bool hit = false;
        foreach (string s in c.Substrings)
        {
            if (line.Contains(s, c.Comparison)) { hit = true; break; }
        }
        if (!hit)
        {
            foreach (var r in c.Regexes)
            {
                if (r.IsMatch(line)) { hit = true; break; }
            }
        }
        if (!hit)
            return false;
        foreach (string x in c.Excludes)
        {
            if (line.Contains(x, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
