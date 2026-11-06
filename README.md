# Revit Journal Analyzer

An offline Windows app that explains Autodesk Revit journal files in plain English: what happened, why, and what to do.
No AI and no uploads: journals are analyzed only on the user's computer, using a rule base and computed metrics.
It runs separately from Revit, so it also works when Revit has crashed or is hung.

## Build (Windows, .NET 10 SDK)

```bat
dotnet build RevitJournalAnalyzer.slnx -c Release
dotnet test tests\RevitJournal.Tests\RevitJournal.Tests.csproj
dotnet publish src\RevitJournal.App\RevitJournal.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\win-x64
```

- `RevitJournalAnalyzer.exe` — the app. It starts with **"What's wrong with Revit?"**: the user picks a symptom (crashed, won't sync, slow) and gets the answer for the relevant session of the selected Revit version, plus a hint when a recent crash or failed sync is found. **History** lists every session grouped by day with a full report each. Drag a journal onto the window or pass it as an argument; `--symptom crashed|wont-sync|slow` opens an answer directly.
- Symptoms and their fallback checklists live in `knowledge-base/symptoms.json`.
- `rja.exe <journal.txt>` — the same report in the console; `rja <folder>|--local` lists sessions; `rja --local --stats` shows how often each rule fires (use it to find noisy rules); `rja --local --symptom slow [--version 2025]` prints the start-screen answer.
- Development from a Mac: `tools/vm.sh sync` copies the tree to the Windows VM, `tools/vm.sh run "<cmd>"` runs a command there.

## Structure

```
src/RevitJournal.Core/   parser, rule engine, metrics, report (the rule base is embedded as resources)
src/RevitJournal.Cli/    rja.exe console tool
src/RevitJournal.App/    WPF app
tests/                   xUnit tests on synthetic journal excerpts
knowledge-base/
  schema.json            rule format
  rules/
    autodesk-help.json   rules from Autodesk Help (Revit Troubleshooting)
    community.json       signatures and fixes from Autodesk forums and BIM blogs
    metrics.json         computed rules (memory, crashes, add-ins, sync)
    observed.json        rules found in real journals
  notes/
    journal-format.md    journal file format reference for the parser
tools/
  validate_kb.py         validates the rule base
  scan.py                quick Python prototype scanner
  vm.sh                  sync/run on the Windows VM
```

## Rules

Each rule has:
- `match`: substrings (`any`) or regexes (`regex`) to look for in journal lines, optional `ignore_case` and `exclude`; computed rules have a `metric` block instead;
- `title`, `cause`, `fix`: explanation and fix steps. The app shows English (`en`); `uk` texts are kept as an optional translation;
- `severity`, `category`;
- `confidence`: `official` (Autodesk documentation), `community-verified` (confirmed forum solution), `community` (unconfirmed);
- `related`: rules from other sources that match the same text; the analyzer shows them as one finding.

Dialog text in journals is translated into the Revit UI language, so rules should match dialog IDs (`'Id : TaskDialog_...`) and command IDs (`ID_...`), not message text.

## Validate

```bash
python3 tools/validate_kb.py
```

## Privacy

Real journal files contain user names, paths, server and project names.
They **must never be committed** to this public repository: `.gitignore` blocks `*.txt`, `samples/` and `journals/`.
