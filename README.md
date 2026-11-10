# Revit Doctor

An offline Windows app that explains Autodesk Revit journal files in plain English: what happened, why, and what to do.
No AI and no uploads: journals are analyzed only on the user's computer, using a rule base and computed metrics.
It runs separately from Revit, so it also works when Revit has crashed or is hung.

## Build (Windows, .NET 10 SDK)

```bat
dotnet build RevitDoctor.slnx -c Release
dotnet test tests\RevitJournal.Tests\RevitJournal.Tests.csproj
dotnet publish src\RevitJournal.App\RevitJournal.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish\win-x64
```

- `RevitDoctor.exe` — the app (called Revit Journal Analyzer up to 0.4.0). It starts on **Right now**: the live state of the computer and of every running Revit. Every session is listed in the sidebar, grouped by day; selecting one opens its full report. Drag a journal onto the window or pass it as an argument; `--symptom crashed|wont-sync|slow` opens an answer directly.
- Symptoms and their fallback checklists live in `knowledge-base/symptoms.json`; a notification opens the matching answer.
- **Overview ("Right now"):** every 5 seconds the app reads the computer's state from Windows — free RAM and page file, processor load, free disk space, and for each running Revit its memory, processor share and GDI/USER objects (Windows allows 10,000 per process). It judges the state (all good / keep an eye on it / act now), forecasts when memory runs out if it keeps falling, tells a busy Revit from a stuck one, and notifies before a limit is reached. The overview opens from the tray icon (left click; the icon carries a green, amber or red dot) and is the left half of the start screen. Thresholds are constants in `HealthMonitor`. Under each Revit it shows the open model's size, linked models and open views against Autodesk's memory guidance; with several Revit versions running the version picker follows them. Free disk space is recorded every few minutes (`%LOCALAPPDATA%\RevitDoctor\disk-history.csv`) so a report can say how full the disk was when a session ended.
- `RJA_SNAPSHOT=<folder>` renders the start screen and the overview to PNG files without showing a window (for checking a build on a desktop that is in use).
- **Worker logs:** `journal.NNNN.workerK.log` (the log of RevitWorker.exe, Revit's helper process) is checked with the same text rules; matching lines are shown as `worker1:<line>`.
- **Advice cites Autodesk:** generated steps and the fixes of new rules end with the Autodesk article that recommends them (`[Autodesk: …]`), and the rule lists that article in its sources.
- **Result cache:** the analysis of a finished session is stored in `%LOCALAPPDATA%\RevitDoctor\cache` (one small file per journal) and reused while the journal's size and time are unchanged and the app build is the same. **Refresh** reads the journals again. The cache stays on the computer and is removed by the uninstaller. `rja --cache <folder>` uses the same cache from the console.
- **Crash or not:** a journal without shutdown lines counts as a crash only with proof — a `.dmp` next to it, a crash record in the journal, or a Windows *Application Error* event for Revit.exe (matched to the journal by the process start time; it also names the faulting module). An *Application Hang* event means Revit froze and was closed. Without proof the session is shown as "closed without shutdown record" (forced close, or a copy of a running session).
- **Look:** a BasePoint Labs app in the style of the FlexiData Configurator — WPF-UI 3.1.1, Manrope (OFL, `src/RevitJournal.App/Fonts`), gray ground with one white card, cobalt primary action, the version next to the name in the title bar and the BasePoint Labs mark with basepointlabs.io in the sidebar. Colors and styles are in `src/RevitJournal.App/Theme.xaml`; the logo and `app.ico` are drawn by `tools/make-icon.ps1`. The version comes from `Directory.Build.props`.
- **Live monitoring:** the app keeps a tray icon and follows the journal of every running Revit (only the new lines, every 5 s). It shows a running-Revit card on the start screen and a Windows notification when a sync fails, a sync takes over 10 min, free RAM drops below 1 GB / 5 %, Windows reports Revit "not responding" for a minute, Revit shows a serious error, or Revit disappears without its shutdown lines. Clicking the notification opens the answer page. Closing the window keeps it in the tray; "Start with Windows" in the tray menu runs it as `--background`. One copy per user session. Running sessions are analyzed once, by the tray agent, from the new lines only; the window shows those results. Set `RJA_INSTANCE=dev` to run a build under test next to an installed copy.
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
