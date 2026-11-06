using System.Diagnostics;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Reporting;

// rja <journal.txt>         full report for one journal
// rja <folder> | --local    one line per journal (folder, or all Revit versions on this PC)
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: rja <journal.txt> | <folder> | --local");
    return 2;
}

Console.OutputEncoding = System.Text.Encoding.UTF8;
var analyzer = Analyzer.CreateDefault();
string target = args[0];

if (File.Exists(target))
{
    var sw = Stopwatch.StartNew();
    var result = analyzer.AnalyzeFile(target);
    Console.WriteLine(TextReport.Render(result));
    Console.WriteLine($"({result.Session.LineCount:N0} lines analyzed in {sw.ElapsedMilliseconds} ms)");
    return 0;
}

var journals = target == "--local"
    ? JournalLocator.FindAll()
    : Directory.Exists(target) ? JournalLocator.FindInFolder(target).OrderByDescending(j => j.LastWrite).ToList() : null;
if (journals is null)
{
    Console.Error.WriteLine($"Not found: {target}");
    return 1;
}

var total = Stopwatch.StartNew();

// rja --local --stats : how often each rule fires across journals, to spot noisy rules
if (args.Contains("--stats"))
{
    var stats = new Dictionary<string, (int Journals, int Hits, string Sample)>();
    foreach (var j in journals)
    {
        foreach (var f in analyzer.AnalyzeFile(j.Path).Findings)
        {
            foreach (string id in f.AlsoMatched.Select(a => a.Id).Prepend(f.Rule.Id))
            {
                var cur = stats.GetValueOrDefault(id, (0, 0, ""));
                string sample = cur.Item3.Length > 0 ? cur.Item3
                    : f.Evidence.FirstOrDefault()?.Text ?? f.Facts.FirstOrDefault() ?? "";
                stats[id] = (cur.Item1 + 1, cur.Item2 + f.HitCount, sample);
            }
        }
    }
    Console.WriteLine($"{"Journals",8} {"Hits",7}  Rule / sample");
    foreach (var (id, st) in stats.OrderByDescending(kv => kv.Value.Journals))
        Console.WriteLine($"{st.Journals,8} {st.Hits,7}  {id}\n{"",18}{Fmt.Trim(st.Sample, 150)}");
    Console.WriteLine($"\n{journals.Count} journals in {total.Elapsed.TotalSeconds:F1} s");
    return 0;
}

var results = new List<AnalysisResult>();
Console.WriteLine($"{"Revit",-6} {"Journal",-18} {"Status",-10} {"Worst",-9} {"#",3}  Headline");
foreach (var j in journals)
{
    try
    {
        var r = analyzer.AnalyzeFile(j.Path);
        results.Add(r);
        string status = r.Status switch
        {
            SessionStatus.Running => "running",
            SessionStatus.ClosedNormally => "closed",
            SessionStatus.ShutdownIncomplete => "exit-hung",
            _ => "ABNORMAL",
        };
        Console.WriteLine($"{j.RevitVersion,-6} {j.Name,-18} {status,-10} {r.WorstSeverity?.ToString() ?? "-",-9} {r.Findings.Count,3}  {r.Headline}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{j.RevitVersion,-6} {j.Name,-18} ERROR      {ex.GetType().Name}: {ex.Message}");
    }
}
if (History.RepeatedCrashes(analyzer.Rules, results) is { } repeated)
{
    Console.WriteLine($"\n[{repeated.Severity.ToString().ToUpperInvariant()}] {repeated.Rule.Title.En}");
    foreach (string fact in repeated.Facts)
        Console.WriteLine($"  • {fact}");
}
Console.WriteLine($"\n{journals.Count} journals in {total.Elapsed.TotalSeconds:F1} s");
return 0;
