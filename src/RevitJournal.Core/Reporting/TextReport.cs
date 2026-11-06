using System.Text;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Rules;

namespace RevitJournal.Core.Reporting;

/// <summary>Plain-text report for the console and the "Copy report" button.</summary>
public static class TextReport
{
    public static string Render(AnalysisResult r, bool includeEvidence = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Headline.ToUpperInvariant());
        sb.AppendLine(Path.GetFileName(r.Session.FilePath));
        sb.AppendLine();

        sb.AppendLine("WHAT HAPPENED");
        foreach (string line in r.Summary)
            sb.AppendLine("  • " + line);
        sb.AppendLine();

        if (r.Findings.Count == 0)
        {
            sb.AppendLine("No known problems were found in this journal.");
        }
        else
        {
            sb.AppendLine($"FINDINGS ({r.Findings.Count})");
            int i = 1;
            foreach (var f in r.Findings)
            {
                sb.AppendLine();
                sb.AppendLine($"{i++}. [{f.Severity.ToString().ToUpperInvariant()}] {f.Rule.Title.En}");
                sb.AppendLine($"   Why: {f.Rule.Cause.En}");
                foreach (string fact in f.Facts)
                    sb.AppendLine($"   • {fact}");
                if (f.Steps.Count > 0)
                {
                    sb.AppendLine("   What to do in your case:");
                    int own = 1;
                    foreach (string s in f.Steps)
                        sb.AppendLine($"     {own++}) {s}");
                }
                sb.AppendLine(f.Steps.Count > 0 ? "   General advice:" : "   What to do:");
                int step = 1;
                foreach (string s in f.Rule.Fix.En)
                    sb.AppendLine($"     {step++}) {s}");
                if (includeEvidence && f.Evidence.Count > 0)
                {
                    sb.AppendLine($"   Journal lines ({f.HitCount} match{(f.HitCount == 1 ? "" : "es")}):");
                    foreach (var e in f.Evidence)
                        sb.AppendLine($"     {e.Line,7} {Fmt.Time(e.Time)}  {Fmt.Trim(e.Text, 160)}");
                }
                sb.AppendLine($"   Source: {ConfidenceLabel(f.Rule.Confidence)} ({f.Rule.Id})"
                              + (f.AlsoMatched.Count > 0 ? $"; also: {string.Join(", ", f.AlsoMatched.Select(a => a.Id))}" : ""));
            }
        }

        if (r.Status != SessionStatus.ClosedNormally && r.LastActions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("LAST USER ACTIONS");
            foreach (var a in r.LastActions)
                sb.AppendLine($"  {Fmt.Time(a.Time)}  {a.CommandId ?? a.Kind}  {Fmt.Trim(a.Text, 100)}");
        }

        return sb.ToString();
    }

    public static string ConfidenceLabel(Confidence c) => c switch
    {
        Confidence.Official => "Autodesk documentation",
        Confidence.CommunityVerified => "confirmed community solution",
        _ => "community report, unconfirmed",
    };
}
