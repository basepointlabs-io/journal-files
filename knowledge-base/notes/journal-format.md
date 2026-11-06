# Revit journal file format: parser reference

Collected 2026-11-06. This is a reference for an **offline, rule-based** analyzer of `journal.NNNN.txt` files.

## How to read this document

Each claim carries a status tag:

* **[V] Verified.** The line was seen verbatim in at least one real journal or real journal excerpt (listed under "Primary samples"). The year is given as R20xx.
* **[P] Parser-derived.** The claim comes from the source code of an existing parser (Journalysis, jeffreyclu, amcgoey). These parsers were written against real journals, but I have not seen the line myself.
* **[I] Inferred.** My own interpretation of observed patterns. Treat it as a hypothesis until real samples confirm it.

Autodesk has never published a specification of the format. Autodesk's own help pages only say that journals exist, where they live, and which strings to search for when diagnosing problems. Everything structural below has been reverse-engineered.

### Primary samples actually inspected (real journals, public)

| Id | Revit | Kind | Source |
|---|---|---|---|
| S-2010 | 2010 | full journal, normal exit | jeremytammik/tbc `a/zip/journal_file.txt` (https://github.com/jeremytammik/tbc/blob/HEAD/a/zip/journal_file.txt) |
| S-2015a | 2015 | full journal, **crash in add-in** (abrupt end) | OTOY forum attachment (https://render.otoy.com/forum/download/file.php?id=38838) |
| S-2015b | 2015 | full journal, normal exit | erikgett/RevitJournalRunner (https://github.com/erikgett/RevitJournalRunner/blob/HEAD/rvt2IFCConverter/Journals/rvt2ifc.txt) |
| S-2016 | 2016 | full journals, normal exit (Dynamo automation) | andydandy74/DynamoAutomation `issues/10/journal.0001.txt`, `issues/multiple runs/journal.0038.txt` (https://github.com/andydandy74/DynamoAutomation) |
| S-2017 | 2017 | excerpt, **fatal error with recovery prompt** | revitpythonshell issue #43 (https://github.com/architecture-building-systems/revitpythonshell/issues/43) |
| S-2019a | 2019 | excerpt, **managed exception in Dynamo** | DynamoRevit issue #2066 (https://github.com/DynamoDS/DynamoRevit/issues/2066) |
| S-2019b | 2019 | worker log (`journal.0019.worker1.log`), normal exit | DaltonGOO/BIM-Analytics-Platform (https://github.com/DaltonGOO/BIM-Analytics-Platform) |
| S-2017..2021 | 2017–2021 | ~19,000 lines of excerpts from dozens of users and locales (EN/ES/FR/ZH) | mcneel/rhino.inside-revit issue #248 (https://github.com/mcneel/rhino.inside-revit/issues/248) |
| S-2024a | 2024.3.4 | full journals (playback-driven), recorded Jan 2026 | DarickBrokaw/BrokawSkills `skills/revit-dynamo-start/assets/journal.000{1,2,3}.txt` (https://github.com/DarickBrokaw/BrokawSkills) |
| S-2024b | 2024 | excerpt: **add-in failed to start**, normal exit (Oct 2026) | bringfire/bim-agent-lab `repeated-startup-journal.txt` and `after-normal-close.json` (https://github.com/bringfire/bim-agent-lab/tree/HEAD/projects/revit-readiness/docs/verification/host-diagnostic-startup-evidence) |

**Gap:** I found no real journal from R2025 or R2026. The Autodesk forums (forums.autodesk.com) and autodesk.com KB articles, where most excerpts are posted, returned 403 to automated fetches, so I could not read them.

### Parsers whose source was read

* **Journalysis** (Andreas Dieckmann), a Dynamo package with a new C# core (`Journalysis.Core`). It is the most complete reverse-engineering available. https://github.com/andydandy74/Journalysis and wiki https://github.com/andydandy74/Journalysis/wiki (pages "JournalLine types" and "Worksharing Log Dictionary").
* **revit-journal-parser** (Jeffrey Lu, Perkins Eastman). https://github.com/jeffreyclu/revit-journal-parser/blob/master/journalparser.py
* **Revit-Journal-Maker** (`rjm`, Ehsan Iran-Nejad, used by pyRevit). It *writes* journals for playback and has **no crash-detection logic**. It is useful only for confirming command and dialog syntax. https://github.com/eirannejad/Revit-Journal-Maker
* **revit_journal_tool.py** (amcgoey/Synthetic-Revit), a small 2026-era tool with shutdown-cleanliness heuristics.
* The page `giobel.github.io/JournalFiles` is an academic summary only. It confirms the mouse-event semantics (`Jrn.MouseMove 0 , 713 , 566`: button state, x, y) and nothing else.

---

## 0. Files, location, encoding

* Location (official, Autodesk help "Journal Files"): `%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 20XX\Journals`. Revit writes one new journal per Revit launch, and the highest number is the latest. https://help.autodesk.com/cloudhelp/2014/ENU/Revit/files/GUID-477C6854-2724-4B5D-8B95-9657B636C48D.htm
* Other files in the same folder (IMAGINiT; Autodesk support replies quoted in search results):
  * `journal.NNNN.worker1.log`: a worker-process journal with the same format, written by R2019+ [V S-2019b, S-2024a]. It is not the main session log, and Autodesk support tells users to ignore it. The analyzer should **skip it for crash detection** but may read it.
  * `journal.NNNN*.dmp`: a crash minidump. **If a dump with the journal's base name exists, the session crashed** [P amcgoey; I].
  * `dump.NNNN.txt`: a text dump.
* Folder name: older products use `Autodesk Revit Architecture 2015`, `Autodesk Revit 2016`, …; R2010 used `C:\Program Files\Autodesk Revit Architecture 2010\Journals` [V S-2010, S-2015a].
* **Line endings:** CRLF in Windows originals [V]. Some copies carry LF only, so the parser must accept both.
* **Encoding [I]:** every sample is plain ASCII. A Chinese-locale excerpt shows mojibake (`'<ϵͳҲָļ>'`) in an exception message, which suggests the **system ANSI code page** (e.g. cp1251 on Ukrainian Windows), not UTF-8/UTF-16. Recommended decode order: UTF-8 strict → if that fails, cp1251 → cp1252 → latin-1 with replacement. (amcgoey tries `utf-8-sig, utf-16, utf-8, cp1252, latin-1`.) **SLOG files (worksharing logs) are UTF-16-LE** [P Journalysis `WorksharingLog.ByPath.py` opens with `encoding='utf-16-le'`].
* **Trailing NULs:** Journalysis strips trailing `\0` on every line, so journals cut by a crash can end in NUL padding [P].
* **A journal of a running Revit session is still open for writing.** It has no end marker yet. Don't call it a crash: check that the file is not the newest one and/or that its mtime is not "now" [I; amcgoey's "UNCERTAIN" state].

---

## 1. Physical vs. logical lines

The file is a **VBScript** program. Journals can be replayed, so every non-comment line is executable `Jrn.*` VBScript [V].

* `'` starts a comment. Almost all diagnostic data is in comments.
* `Dim Jrn` / `Set Jrn = CrsJournalScript` appear once near the top [V all].
* **Leading whitespace is significant to humans only.** Revit indents nested operations by 1–6 spaces. Journalysis `TrimStart()`s every line, and the parser should too [V, P].
* **Continuation lines:**
  * VBScript continuation: a line ending in ` _` continues on the next line, which usually starts with `,` [V]:
    ```
    Jrn.Directive "Version"  _
            , "2015.000", "2.146"
    ```
    R2024 moves the key onto the continuation line [V S-2024a]:
    ```
    Jrn.Directive  _
            "Version"  , "2024.000" , "2.179"
    ```
    Join rule (Journalysis): if the previous record ends with `_`, append the next line. If a line starts with `,`, append it to the previous record and replace ` _,` with `,`.
  * Multi-line comments: the following lines start with `'` immediately followed by text (no space), e.g. the hardware-flags block and long API messages. Join such a line to the previous **timestamp** or **API message** record when it follows one [P Journalysis `HandleApostropheLine`]:
    ```
    ' 0:< 
    'Can use hardware=y
    'Hardware support flags:
    'DrvOlderThanTested=n
    ```
* **Blocks:** Journalysis splits the file into "blocks", each starting at a timestamp line. Lines before the first timestamp (the `Initial VM` line) are block 0. Every record inherits the time of its block [P]. This is the basis for all timing.

---

## 2. Line types

### 2.1 Timestamp lines `'C`, `'E`, `'H`

Shape [V everywhere]:
```
'C 18-Dec-2014 13:23:34.435;   0:< started recording journal file
 'E 18-Dec-2014 13:23:43.337;   0:< 
'H 18-Dec-2014 13:23:40.344;   0:< 
'C 20-Jan-2026 17:29:09.299;  started recording journal file          (R2024: no "0:<")
'C 21-Jul-2020 15:34:50.830;  DBG_INFO: Could not get material property type from RenderingAsset! Name is GenericSchema.: line 247 of d:\ship\2020_px64\source\revit\revitdb\rvtlink\externalfileutils.cpp.
'C 21-Jul-2020 15:34:50.543;  [00001740]DBG_INFO: Invalid path detected in file exists task: ...
```
Regex (tolerant):
```
^\s*'\s?([CEH])\s+(.+?);\s*(?:(\d+):<\s*)?(.*)$
```
* The separator between marker and date may be `'C ` or `' C ` [P Journalysis supports both].
* **Date format is LOCALE-DEPENDENT** [V S-2017..2021]. Observed: `15-May-2020` (EN), `18-jun-2020` (ES, lowercase), `15-sept.-2020` (FR, with a dot), `04-8-2020` (numeric month, ZH/JP locale). Time is always `HH:MM:SS.mmm`, 24-hour. The parser needs a month table for EN/UK/RU/DE/FR/ES/IT…, a numeric-month fallback, and must accept upper/lower case and a trailing dot. (For a Ukrainian-locale Windows expect Ukrainian or Russian abbreviations. **This is unverified and needs a sample.**)
* There is **no timezone** in timestamps. They are local time [I].
* **Meaning of C/E/H.** Not documented. Journalysis's author: "no idea what those letters stand for". Empirically [V, I]:
  * `'E`: precedes a **user-input playback entry**: `Jrn.Command`, `Jrn.MouseMove`/`LButton*`, `Jrn.Key`, `Jrn.RibbonEvent`, `Jrn.Activate`, `Jrn.Size`, `Jrn.AddInEvent`, `Jrn.AppButtonEvent`. It also precedes `' [Jrn.BasicFileInfo]` and `' [Jrn.CloudModelCache]`. Think of it as "Event".
  * `'H`: precedes **data/directive** entries that Revit records so playback can answer questions without UI: `Jrn.Data`, `Jrn.Directive`, `Jrn.Data "File Name"`, `Jrn.Data "TaskDialogResult"`. Think of it as "Handler/hook".
  * `'C`: a **log comment** with free text (start/stop of internal phases, license, DBG_ messages, exception codes, "File: Open", "finished recording journal file").
* `N:<` after the semicolon is a **nesting depth** (0–10+) that increases inside nested operations, for example `' 6:<` during file open [V, I]. Ignore it for parsing. It can serve as a weak hint of how deep the stack was at a crash.

### 2.2 Plain comment lines `' N:< text`

These hold everything else: memory, API messages, system info, SLOG, and so on.
```
' 0:< Autodesk Revit 2024 
' 0:< 64-bit load point = C:\Program Files\Autodesk\Revit Architecture 2015 
' 0:< this journal = C:\Users\<user>\AppData\Local\Autodesk\Revit\Autodesk Revit Architecture 2015\Journals\journal.0010.txt 
```
Some comments have no depth, e.g. `' Build: ...` and `'Warning: Event timer reset skipped - TrustedUtility::readStreams` [V].

### 2.3 Elapsed-time lines (operation durations) [V, I]

These are high-value for timing. Revit writes `' <seconds> <depth>:<<<name` lines when an internal timed operation ends:
```
'  0.104702   1:<<<DictionaryAnalysis::initialize 
'  8.515956   1:<<Open File 
'  0.232272    2:<<OpenFileForSave::openFileForSave 
' 13.081313   1:<<<wait for ForgeUnitsAdapter::configureFolders 
' 92.985045  0:<<Event timer/ALL_GAPS  92.985045/TurnOff_GAPs 1% 
' 45.315397   1:<<API External Command Time 
' 19.293582!!! 2:!!!BIG_GAP API External Command Time 
'  5.087322!!!! 3:!!!BIG_GAP [Cloud]openOntoDesktop 
'  1.135326!!! 2:!!!BIG_GAP API External Command Time 
```
Regex: `^\s*'\s*(\d+\.\d+)(!*)\s+(\d+):(?:!!!BIG_GAP\s+|<+)(.+?)\s*$`
* The first number is the **duration in seconds** of the named operation [I, consistent with the "API External Command Time" values matching gaps between timestamps].
* `!!!BIG_GAP` marks an operation Revit itself considered unusually long. It is used for anything from ~1 s upward [V].
* **`API External Command Time` is the run time of an add-in's external command** (`IExternalCommand.Execute`) [V S-2017, S-2017..2021: it always follows a `Jrn.RibbonEvent "Execute external command:..."`]. Values up to 1359 s were observed. This is the direct way to measure add-in command time.

### 2.4 `Jrn.*` playback lines [V]

Prefix → meaning (list from Journalysis enums, all forms seen in samples):

| Prefix | Example (verbatim) |
|---|---|
| `Jrn.Command "<source>" , "<description> , <ID>"` | `Jrn.Command "SystemMenu" , "Quit the application; prompts to save projects , ID_APP_EXIT"` |
| | `Jrn.Command "Ribbon"  , "Open an existing project , ID_REVIT_FILE_OPEN"` (R2024: extra spaces) |
| | `Jrn.Command "AccelKey" , "Save the active project , ID_REVIT_FILE_SAVE"` |
| | `Jrn.Command "Ribbon" , "Guardar el proyecto activo , ID_REVIT_FILE_SAVE"`. **The description is localized and the ID is not, so always key on the ID** |
| | `Jrn.Command "StartupPage" , "Open this project , ID_FILE_MRU_FIRST"` / `Jrn.Command "Internal" , "Show or hide recent files , ID_STARTUP_PAGE"` |
| `Jrn.Data "<key>" , values…` | `Jrn.Data "TaskDialogResult", "Do you want to save changes to DummyModel_R2016.rfa?", "Yes", "IDYES"` |
| | `Jrn.Data "File Name"  , "IDOK" , "2024templaterevitskill.rte"` (on two physical lines in R2024) |
| | `Jrn.Data "Transaction Successful" , "Wall"` / `"Transaction Successful"  , "Dynamo-51297CB5 Script"` |
| | others: `"MRUFileName"`, `"Dropped File Name"`, `"ProgressCancelled"`, `"WorksetConfig"`, `"Error dialog"` [P jeffreyclu], `"JournalDefaultTemplate"`, `"APIStringStringMapJournalData"` |
| `Jrn.Directive "<key>" , values…` | `"Version" , "2015.000", "2.146"`, `"Username" , "Owner"`, `"WindowSize"`, `"DocSymbol"`, `"IdleTimeTaskSymbol"`, `"AllowLinkSelection"`, `"DebugMode"` |
| Mouse | `Jrn.MouseMove    0 ,    552 ,    708`, `Jrn.LButtonDown    1 ,    231 ,    146`, `Jrn.Wheel`, `Jrn.Scroll`, R/M/L Button Up/Down/DblClk |
| Keys | `Jrn.Key    8 , "VK_CONTROL" , 29` |
| UI events | `Jrn.RibbonEvent "TabActivated:Add-Ins"`, `Jrn.RibbonEvent "Execute external command:CustomCtrl_%CustomCtrl_%Add-Ins%Rhinoceros%CommandRhinoInside:RhinoInside.Revit.UI.CommandRhinoInside"`, `Jrn.RibbonEvent "Execute external command:41d34ec8-525a-4147-bde0-076f05a1b93f:OctaneRenderPlugin.Command"` |
| | `Jrn.Activate "[]" , "Legend: 00.1_MODEL DISTRIBUTION SHEET"`, `Jrn.Size        0 ,    836 ,    433`, `Jrn.AppButtonEvent 1 , "Application Menu is opening"` |
| | Dialogs: `Jrn.PushButton "Modal , Opening Worksets , Dialog_Revit_Partitions", "OK, IDOK"`, `Jrn.CheckBox`, `Jrn.ComboBox`, `Jrn.RadioButton`, `Jrn.Edit`, `Jrn.ListBox`, `Jrn.TabCtrl`, `Jrn.TreeCtrl`, `Jrn.Grid`, `Jrn.Browser`, `Jrn.Close`, `Jrn.Maximize`, `Jrn.Minimize`, `Jrn.Restore`, `Jrn.PropertiesPalette`, `Jrn.SBTrayAction`, `Jrn.SliderCtrl`, `Jrn.WidgetEvent`, `Jrn.DropFiles` |
| Add-in journaling | `Jrn.AddInEvent "AddInJournaling" , "WpfWindow(_this,Dynamo).WpfButton(0,RunButton).Click()"` |

`Jrn.Command` source tokens: `AccelKey, Internal, KeyboardShortcut, PrintPreviewUI, Ribbon, StartupPage, StatusBar, SystemMenu` [P]. Older journals and hand-written ones also use `Menu` and `DesignBar` [V S-2010-era dorevit blog, rjm dev scripts].

Useful command IDs (from jeremytammik/tbc `CommandIds.txt`, https://github.com/jeremytammik/tbc/blob/HEAD/a/zip/CommandIds.txt):
`ID_REVIT_FILE_OPEN` (Open), `ID_APPMENU_PROJECT_OPEN`, `ID_FILE_MRU_FIRST`… (recent files), `ID_REVIT_FILE_SAVE` (Save), `ID_REVIT_FILE_SAVE_AS`, `ID_REVIT_FILE_CLOSE`, `ID_APP_EXIT` (Exit), **`ID_FILE_SAVE_TO_MASTER`** (Synchronize and Modify Settings), **`ID_FILE_SAVE_TO_MASTER_SHORTCUT`** (Synchronize Now), `ID_WORKSETS_RELOAD_LATEST`, `ID_RELINQUISH_ALL_MINE`, `ID_REVIT_MODEL_BROWSER_OPEN` (R2019+ Home), `ID_STARTUP_PAGE` (≤R2018 start page) [P Journalysis StartupTime].

### 2.5 Dialogs and messages inside comments [V]

```
' 0:< TaskDialog "Revit cannot run the external application "BIM Agent Lab readiness bridge". Contact the provider for assistance.  Information they provided to Revit about their identity: BIM Agent Lab."
' 0:< TaskDialog "Keyboard shortcut 'R' is now assigned to Rhino"
' 1:< Unconverted MessageBox "A fatal error has occurred.  The application will be terminated.  You have the opportunity to save recovery files for all of your changed projects. 
'
'Would you like to save a recovery file?" 
 ' [Jrn.AutoConvertedMessageBox] Rvt.Attr.MessageId: IDS_UNRECOVERABLE_ERROR Rvt.Attr.MessageAnswer: IDNO 
```
Seen only in parser code [P jeffreyclu]: `TaskDialog "Revit will use raster printing…`, the message `"An unrecoverable error has occurred.  The program will now be terminated.  All of your data has been recently saved, so there is no need to create recovery files."`, `"You cannot synchronize to central until the model is repaired."`, `is missing many elements, and it cannot be opened.`, `A serious error has occurred. The current action has been cancelled`, `Jrn.Data "Error dialog"` after an `'H` line, and `"fatal error"` two lines after an `'H` line. "Error posted" is mentioned on giobel's page, but I did **not** see it verbatim. Treat it as unverified.

### 2.6 `DBG_WARN` / `DBG_INFO` / `DBG_ERROR` [V for WARN and INFO; P for ERROR]

They can appear on `'C` timestamp lines or on depth comments:
```
' 1:< DBG_WARN: Strangely, there is no view currently active in the given 'active ' document: line 257 of Objects\APIUIDocumentHandwritten.cpp. 
' 2:< DBG_INFO: refineSurfCurveIntersection failed in Surface::intersectWithLine; trying the less robust intersectSurfWithLineByNewton as a backup.: line 3461 of Surface.cpp. 
'C 20-Jan-2026 17:30:16.177;  DBG_INFO: The addin file -Dynamo.addin- in current user folder is duplicated.: line 466 of F:\Ship24.3.4\2024_px64\Source\API\APIInterop\AddIn\AddInManagerUtils.cpp. 
    'DBG_INFO: journal has not specified screen resolution yet: line 427 of ...JournalDesktopView.cpp. 
```
Shape: `DBG_(INFO|WARN|ERROR): <message>: line <n> of <source path>.` The source path shows the Autodesk build tree (`F:\Ship24.3.4\2024_px64\...`) and is not user data. Most DBG_INFO lines are noise. Autodesk's fatal-error help page lists specific DBG_ strings worth flagging (signature rules, not this file).

### 2.7 Structured `[Jrn.xxx]` attribute comments [V]

```
' [Jrn.BasicFileInfo] Rvt.Attr.Worksharing: Central Rvt.Attr.UserName:  Rvt.Attr.CentralModelPath: SOMEPATH\MODELNAME.rvt Rvt.Attr.RevitBuildVersion: Autodesk Revit 2017 (Build: 20170419_0315(x64)) Rvt.Attr.LastSavePath: SOMEPATH\MODELNAME.rvt Rvt.Attr.ProjectSpark: notProjectSpark Rvt.Attr.LocaleWhenSaved: ENU 
' [Jrn.BasicFileInfo] Rvt.Attr.Worksharing: File-Based Central Rvt.Attr.Username:  Rvt.Attr.CentralModelPath: \\SERVER\SHARE\...\WB_1462_CentralFile.rvt Rvt.Attr.RevitBuildVersion: Autodesk Revit 2020 (Build: 20190327_2315(x64)) Rvt.Attr.LastSavePath: \\SERVER...
' [Jrn.BasicFileInfo] Rvt.Attr.Worksharing: Not enabled Rvt.Attr.Username:  Rvt.Attr.CentralModelPath:  Rvt.Attr.RevitBuildVersion: Autodesk Revit 2024 (Build: 20250918_1515(x64)) Rvt.Attr.LastSavePath: R:\APP\RVT\...\00cc4d3c304d423fbaeeef5f1f6df8de.rte Rvt.Attr.LTProject: notLTProject Rvt.Attr.LocaleWhenSaved: ENU Rvt.Attr.FileExt: rte 
' [Jrn.CloudModelCache] Rvt.Attr.CacheFolderSize: 0 Rvt.Attr.SizeOfCacheModelsIn30Days: 0 Rvt.Attr.UseDefaultCacheLocation: 1
' [Jrn.AutoConvertedMessageBox] Rvt.Attr.MessageId: IDS_UNRECOVERABLE_ERROR Rvt.Attr.MessageAnswer: IDNO 
' [Jrn.ESSchemaOpenRoster] 0Rvt.Attr.StoredSchemas:  
```
Parse rule: split on `Rvt.Attr.`, then `key: value` on the first `:`. Values can be empty, and lines can be truncated, so be lenient [P Journalysis issue #57]. `Worksharing` values seen: `Central`, `Local`, `Not enabled`, `File-Based Central` [V, P]. The `RevitBuildVersion` in BasicFileInfo is **the build that last saved the file**, not the running build. Compare it with the header to detect upgrades and mixed builds.

Related plain comments: `' 6:< File was saved in Autodesk Revit 2024 (Build: 20250918_1515(x64))` and a `Document save history` block [V, P Journalysis `LoadedModel.Version/VersionHistory`].

---

## 3. Header: version, build, user, machine, OS, CPU, RAM, GPU

Typical R2024 start [V S-2024a]:
```
' 0:< Initial VM: Avail 134213270 MB, Used 24 MB, Peak 24; RAM: Avail 102533 MB, Used 66 MB, Peak 24 
' 0:< GUI Resource Usage GDI: Avail 9997, Used 3, User: Used 1 
'C 20-Jan-2026 17:29:09.299;  started recording journal file 
' Build: 20250918_1515(x64)
' Branch: RELEASE_2024.3.4
' Release: 2024.3.4
Dim Jrn
Set Jrn = CrsJournalScript
```
R2015 start [V S-2015a]:
```
' 0:< Initial VM: Avail 8388384 MB, Used 19 MB, Peak 46; RAM: Avail 29326 MB, Used 40 MB, Peak 46 
'C 18-Dec-2014 13:23:34.435;   0:< started recording journal file 
' Build: 20140606_1530(x64)
' Branch: RELEASE_2015_RTM
```
R2010 [V S-2010]: `' 0:< Initial VM measure: 1936 MB available, 37 MB used ` then `' Build: 20090317_2115` (no Branch).

| Field | Where / regex | Status |
|---|---|---|
| Journal start time | `'C <ts>; [0:<] started recording journal file` | V |
| Build | `^' Build:\s+(\d{8}_\d{4})(\(x64\))?` (date_time of build, e.g. `20250918_1515` = 2025-09-18) | V |
| Branch | `^' Branch:\s+(\S+)` e.g. `RELEASE_2015_RTM`, `RELEASE_2016_SUNRISE`, `RELEASE_2019`, `RELEASE_2024.3.4` | V |
| Release (update) | `^' Release:\s+(\S+)` e.g. `2019.2.2`, `2024.3.4`. Present from about R2019 | V |
| Product / year | `' 0:< Autodesk Revit 2024 ` or `' 0:< Autodesk Revit Architecture 2015 `; also `Jrn.Directive "Version" , "2024.000" , "2.179"` (first 4 chars = year) | V |
| Install path | `' 0:< 64-bit load point = C:\Program Files\Autodesk\Revit 2024` | V |
| Journal path | `' 0:< this journal = C:\Users\<winuser>\AppData\Local\Autodesk\Revit\Autodesk Revit 2015\Journals\journal.0010.txt` | V |
| Username (Revit/Autodesk ID) | `Jrn.Directive "Username" , "Owner"` (2 physical lines; key on the 1st line ≤R2019, on the 2nd line in R2024). Value examples: `"andydandy"`, `"DaltonGoodwin6367750174"`, `"dbrokaw44TRW"`. Since ~R2015 signed-in users get their Autodesk ID | V |
| Machine name | `' 3:< Additional IP address/name found for host <HOST>: <IP>` (Journalysis's method, first occurrence). Also SLOG `host=` (in .slog), and cloud `User's Machine Name WKS-...` | V |
| Language | `' 0:< Actual language code=ENU` (R2024) | V |
| License | `' 0:< License mode: Standalone` / `User`, `Status: …`, `Expiration: …` | V |
| .NET | `' 0:<    4.8.09221(533509)=dotNetInstalledVersionString` (R2024 SystemSettings block) | V |
| CPU cores | `' 0:< ...ParallelCores = 16` (Log Summary block) | V |
| GPU + driver | `' 1:< VIDEO CARD ENVIRONMENT: CARD="NVIDIA RTX A5000 Laptop GPU" ManufacturerID="10DE" DeviceID="24B6" osID="80000" DRIVER="32.0.15.8160" DEVICE="DX11" FL_Caps="50" (Shader Model) ` | V (R2015–R2024) |
| Driver certification | `'Current driver version: 32.0.15.8160` / `'Certified driver(s): …` | V |
| GPU capability flags | block after `' 0:< ` : `'Can use hardware=y`, `'DrvOlderThanTested=n`, `'DrvNewerThanTested=n`, `'CardIsNotListed=y`, `'NotCertifiedEnv=n`, `'SoftwareModeForced=n`, (≤R2016) `'3GBSwitchIsOn=n`, `'FeatureLevelLow=n` | V |
| Graphics mode | `' 0:< Revit Graphics Information` / `' 0:< 	Current Mode : DirectX Hardware` / `Use Hardware: ON`; `' 1:< DX Device: DirectX 11, Hardware`; R2024 `'OGS Device Memory Size: 81305 MB` | V |
| WPF render mode | `' 0:< Setting WPF rendering mode to SoftwareOnly` | V |
| OS / CPU / GPU / printers (WMI dump) | Sections `' 0:< OPERATING SYSTEM INFORMATION:`, `PROCESSOR INFORMATION:`, `VIDEO CONTROLLER INFORMATION:`, `PRINTER INFORMATION:`, `PRINTER CONFIGURATION INFORMATION:` then lines `' 0:<    Key : Value` | V R2010, R2015 only |

WMI dump lines [V S-2015a]:
```
' 0:< OPERATING SYSTEM INFORMATION:  
' 0:<    Caption : Microsoft Windows 7 Professional  
' 0:<    CSDVersion : Service Pack 1 
' 0:<    FreePhysicalMemory : 29679988 
' 0:<    MaxProcessMemorySize : 8589934464 
' 0:<    TotalVirtualMemorySize : 67102056 
' 0:<    TotalVisibleMemorySize : 33551960 
' 0:<    Version : 6.1.7601 
' 0:< PROCESSOR INFORMATION:  
' 0:<    Name : Intel(R) Xeon(R) CPU           X5650  @ 2.67GHz 
' 0:< VIDEO CONTROLLER INFORMATION:  
' 0:<    Caption : NVIDIA Quadro 600 
' 0:<    DriverDate : 20141024000000.000000-000 
' 0:<    DriverVersion : 9.18.13.4105 
```
Key/value split on `' : '`. Memory values are **KB** (`TotalVisibleMemorySize : 33551960` ≈ 32 GB). Multiple processor, video and printer sections may repeat (one per socket/device) [V].

**Important [V, I]:** in the R2016 (automation), R2019 (worker) and R2024 samples the WMI dump is **absent**. In R2015 it was written after `'C …; 0:< appPriv idle`, i.e. at the first idle. So the dump either moved or disappeared in newer versions, or is skipped in short or automated sessions. **Do not rely on it.** Derive total RAM from `RAM Statistics` (§4.2), and the GPU from `VIDEO CARD ENVIRONMENT`. Real R2022–R2026 interactive journals are needed to settle this.

---

## 4. Memory

### 4.1 Delta lines (most frequent)

R2015–R2024 [V]:
```
' 0:< ::0:: Delta VM: Avail -39 -> 8388345 MB, Used +5 -> 24 MB; RAM: Avail -11 -> 29315 MB, Used +12 -> 53 MB, Peak +12 -> 53 MB 
' 0:< ::0:: Delta VM: Avail -52 -> 134217124 MB, Used +5 -> 68 MB, Peak +5 -> 68 MB; RAM: Avail -48 -> 8680 MB, Used +13 -> 148 MB, Peak +13 -> 148 MB 
' 4:< ::1348:: Delta VM: Avail -154 -> 134209680 MB, Used 1426 MB; RAM: Avail -1 -> 20679 MB, Used +0 -> 1605 MB 
' 7:< ::62:: Delta VM: Avail 134178264 MB, Used +0 -> 580 MB; RAM: Avail -12 -> 100600 MB, Used +2 -> 660 MB 
```
* `Field delta -> absolute MB`. **The value after `->` is the current absolute value** [P Journalysis]. When there is no change the delta and arrow are omitted (`Used 1426 MB`). The `Peak` part is present only when the peak changed.
* `VM` is process **virtual address space**. On 64-bit, Avail ≈ 134,217,727 MB (128 TB) on Win 8.1+ or 8,388,607 MB (8 TB) on Win 7. **VM Avail is useless as a "low memory" signal on 64-bit.** VM Used ≈ committed memory of Revit.
* `RAM Avail` = **free physical RAM of the whole machine**. `RAM Used` / `RAM Peak` = **Revit's own working set** and its running peak [I, consistent with RAM Statistics below].
* `::N::` is an increasing counter, probably the number of events or blocks. Ignore it.
* R2010 older form [V]: `' 0:< VM Delta: Available +17 MB -> 1398 MB, Used -2 MB -> 293 MB`.
* Regex per field: `(Avail|Used|Peak)\s+(?:([+-]\d+)\s+->\s+)?(\d+)` applied separately to the part before `RAM:` (VM) and after it (RAM).

`Initial VM` (first line) [V]: `' 0:< Initial VM: Avail 8388384 MB, Used 19 MB, Peak 46; RAM: Avail 29326 MB, Used 40 MB, Peak 46 `. Note that `Peak` carries no `MB`, and the RAM Peak value is sometimes copied from VM (e.g. `Used 66 MB, Peak 24`), so ignore Initial RAM Peak.

### 4.2 RAM / VM Statistics block (snapshot with totals)

[V R2015 crash, R2016 exit, R2019 crash, R2020 save, R2024 open]
```
' 1:<   System (MB) [Available /  Total ]  [Revit Memory Usage (MB)   ] 
' 1:< RAM Statistics:   100657 /   130257       638=InUse      643=Peak  
' 1:< VM  Statistics: 134178293 / 134217727       560=InUse      566=Peak  
```
Regex: `(RAM|VM)\s+Statistics:\s+(\d+)\s*/\s*(\d+)\s+(\d+)=InUse\s+(\d+)=Peak`
* RAM: `Available / Total` = machine free / **machine total physical RAM (MB)**. `InUse`/`Peak` = Revit's current and peak working set (MB).
* When it is written [V]: on **Save**, **File Open** (after `Jrn.Data "File Name"`), **Exit** (several times around "Destroy Display Manager"), and **immediately after an exception** (`ExceptionCode=…`). So the last `RAM Statistics` before a crash is the memory state at the moment of the crash.
* `total RAM` from this line is the most reliable machine-RAM figure across versions [I].

### 4.3 GDI resources

[V R2017+] `' 1:< GUI Resource Usage GDI: Avail 9101, Used 899, User: Used 489`. Avail + Used = 10000 in all samples, which matches the default Windows per-process GDI-object limit of 10,000. The trailing `User: Used` counts USER objects. It is written right after almost every `Delta VM` line in R2019+.

### 4.4 Computing memory metrics

* `peak_revit_ram_mb = max(all RAM Peak from Delta lines, all RAM Statistics Peak)`.
* `total_ram_mb = max(RAM Statistics Total)`. Fallbacks: `TotalVisibleMemorySize/1024` (WMI, old versions); or estimate as `RAM Avail + RAM Used` from Initial VM (a lower bound).
* `min_ram_avail_mb = min(RAM Avail over Delta lines and RAM Statistics Available)`, with the timestamp of its block.
* `last_ram_avail_before_end`: the last value in the final 200 lines.
* `peak_share = peak_revit_ram_mb / total_ram_mb`.
* `min_gdi_avail = min(GDI Avail)`.
* Reference thresholds: the Autodesk Revit Clinic blog (2009, Autodesk support) said "you don't want to see your available memory dip below 100 MB". Autodesk help (2020) recommends ≥16 GB RAM and RAM ≈ 20× the compacted central file size. Restart Revit before memory-heavy tasks. https://help.autodesk.com/cloudhelp/2020/ENU/Revit-Customize/files/GUID-C395AAC8-B5E2-40A5-8B48-1BFEEA9116D6.htm

---

## 5. Timing

General rule [P Journalysis, V]: give each record the time of its **block** (the nearest preceding `'C/'E/'H` timestamp). Then:

* **Session duration** = last timestamp − first timestamp (Journalysis `SessionTime`), or `finished recording journal file` − `started recording journal file` (jeffreyclu).
* **Startup time** = time of the block of the first `Jrn.Command` with ID `ID_REVIT_MODEL_BROWSER_OPEN` (R2019+), `ID_STARTUP_PAGE` (≤R2018), `ID_FILE_MRU_FIRST` or `ID_REVIT_FILE_OPEN`, minus the time of block 1 [P Journalysis `StartupTime`]. Alternative: `'C …; <-DesktopMFCApp::doStartupWarnings` [V] marks the end of startup UI init.
* **Model open duration** (Journalysis `LoadedModelAnalyzer`):
  * start = block time of `Jrn.Command … ID_REVIT_FILE_OPEN` / `ID_APPMENU_PROJECT_OPEN` / `ID_FILE_MRU*`, or `Jrn.DropFiles`. The path is in the next `Jrn.Data` with key `File Name` / `MRUFileName` / `Dropped File Name` (skip it when the value is `IDCANCEL`, or when `ProgressCancelled` is followed by `TaskDialogResult … IDYES`).
  * end = the first timestamp after the comment line `'***********************************************************` that follows the open (Journalysis's "Loaded" marker [P]).
  * There are also direct duration lines: `'  8.515956   1:<<Open File` [V], `[Cloud]openOntoDesktop` BIG_GAP [V], and SLOG `>Open` … `<Open` (+ `>WSConfig`…`<WSConfig`) [P].
  * The file-open phase marker is `'C …;  File: Open` [V].
* **Save**: `Jrn.Command … ID_REVIT_FILE_SAVE` → next timestamp(s). Also `'  0.232272    2:<<OpenFileForSave::openFileForSave` [V].
* **Synchronize with Central**: see §7. Prefer the SLOG `>STC`/`<STC` pair. Otherwise use the command `ID_FILE_SAVE_TO_MASTER(_SHORTCUT)` (or the older `Jrn.PushButton "Modal , Synchronize with Central , Dialog_Revit_PartitionsSaveToCentral", "OK, IDOK"` [P jeffreyclu/rjm]) up to the next user `'E` event.
* **Add-in command time**: `API External Command Time` lines (§2.3), attributed to the preceding `Jrn.RibbonEvent "Execute external command:<ctrl>:<Class>"`.
* **Long gaps**: any gap between consecutive timestamps > N minutes while no `'E` user events occur can mean a hang or a long operation. Gaps between `'E` events are just the user being idle [I].

---

## 6. Files opened / saved / local file / central path

* Opened path: `Jrn.Data "File Name" , "IDOK" , "<path>"` (path may be relative to the journal folder in playback) [V]. `Jrn.Data "MRUFileName"` [V S-2016].
* Model metadata: `' [Jrn.BasicFileInfo] …` (§2.7): `CentralModelPath`, `LastSavePath`, `Worksharing`, `RevitBuildVersion`, `LocaleWhenSaved`, `FileExt` [V].
* Local file creation [V S-2017..2021]:
  ```
  ' 0:< createNewLocal: starting to create a new local file from "J:\...\<ProjectName>.rvt". 
  ' 0:< createNewLocal: finished creating the new local file from "J:\...\<ProjectName>.rvt". 
  ```
* Cloud (BIM 360/ACC) [V]: `' 0:< ModelPath Created: Is server path = True, Central server = "https://model15.skyscraper.autodesk.com:443/ModelService2015/ModelService.svc/basichttpbuffer", Path = "CLD:\\{guid}Project\{guid}…"`. Paths start with `CLD:\\` (cloud) or `RSN://` (Revit Server) [I].
* File size [V once, R2020]: `' 5:< Fix Missing Element For D:\…\<ProjectName>.rvt: File Size: 16232448, Element Count: 5671 `. jeffreyclu also parses `fileSizeOnOpen` following an `Open:Local` line [P].
* Worksharing log connection [V]: `' 3:< Connecting session [$c7a83e8b] to Worksharing Log [<path>]`.

---

## 7. Worksharing (SLOG) events

The central model's `<Central>_backup\<name>.slog` file records worksharing events (UTF-16-LE). **The same events are mirrored into the journal** as comments [V]:
```
' 7:< SLOG $931175d6 2017-11-07 08:38:14.274 >Session  $931175d6
' 1:< SLOG $7640060f 2020-05-18 09:15:24.078 .LockUsers  R locked 
' 0:< SLOG $7640060f 2020-05-18 09:15:24.101 >Auto 
' 3:< SLOG $7640060f 2020-05-18 09:15:24.127 .Auto:LockUsers  R unlocked 
' 0:< SLOG $7640060f 2020-05-18 09:15:24.138 <Auto 
```
Regex: `:<\s*SLOG\s+(\$[0-9a-f]{8})\s+(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+(.*)$`. SLOG timestamps are ISO and locale-independent [V], which makes them the best source for worksharing timing.

Grammar (Journalysis wiki "Worksharing Log Dictionary"; Autodesk Factory meanings): `>X` = start of action X, `<X` = end, `.X` = info inside an action, `$xxxxxxxx` = session id. Terms: `Session`, `Open`, `Open:Central`, `OpenLink`, `WSConfig`, `WSD` (workset dialog), **`STC` (Synchronize with Central)**, `STL` (save to local), `RL` (reload latest; also `STC:RL:Read` inside STC), `ReconnectInMiddle`, `RelinquishAllMine`, `ERN` (editing requests), `ME` (make editable), `EOTC`, `AJ`/`ROB` (autojoin borrowing), `SuggestSTC`, **`Crash`**, `EED`, `Auto` (automatic lock checks).

Sync computation [P Journalysis `GetSyncEvents`]:
* start at `>STC`, end at `<STC`, duration = end − start;
* `ReloadLatestCount` += 1 for every `>STC:RL:Read` inside;
* **aborted** if `.STC:RL:LockRoot RW gaveUp` appears;
* `>STC` without `<STC` before the end of the file means the sync was interrupted (crash or kill) [I].
* Load time = `<Open` − `>Open` (+ `<WSConfig` − `>WSConfig`). Linked model load = `>OpenLink "<path>"` … `<OpenLink`.

SLOG session header lines (in the .slog file, not the journal) [P]: `user="…"`, `build="2024 20250918_1515(x64)"` (year + build), `journal="…journal.0012.txt"`, `host=<ip> "<HOSTNAME>"`, `server=<ip> "<SERVER>"`, `central="\\server\share\model.rvt"`. These lines carry heavy PII.

In-journal cloud worksharing details [V]: `' 0:< Operation - ReadServerPermissionUpdatesTask: ModelSessionToken: User's Name <name>, User's Machine Name WKS-VIE-002, Operation GUID …, Central server …`.

---

## 8. Add-ins

### 8.1 Load / registration messages

`API_SUCCESS` / `API_ERROR` wrappers (R2016+) [V]: `' 0:< API_SUCCESS { <text> } `. The text may span several physical lines, all starting with `'`, until the closing `}`.

| Message | Example (verbatim) |
|---|---|
| UI app started (R2016+) | `' 0:< API_SUCCESS { Starting External Application: RaaS, Class: Autodesk.RaaS.RevitAddin.EApplication, Vendor : ADSK(Autodesk, www.autodesk.com), Assembly: RaaSApplication.dll } ` |
| (R2024 adds version) | `' 0:< API_SUCCESS { Starting External Application: AutoLoad, Class: AutoLoader.AutoLoader, Vendor : ADSK(), Assembly: AutoLoader.dll,  Assembly Version: 24.3.40.26 } ` |
| DB app started | `' 0:< API_SUCCESS { Starting External DB Application: ATF Translation Server, Class: ATFTranslationServer.ATFTranslationServerRegister, Vendor : ADSK(Autodesk, www.autodesk.com), Assembly: C:\Program Files\Autodesk\Revit 2024\…` |
| **App failed to start** | `' 0:< API_ERROR { Starting External Application: BIM Agent Lab readiness bridge, Class: BimAgentLab.RevitReadiness.BridgeApplication, Vendor : BIAL(BIM Agent Lab), Assembly: C:\Users\<user>\AppData\Roaming\Autodesk\Revit\Addins\2024\BimAgentLab.RequiredInformation\BimAgentLab.RevitReadiness.dll,  Assembly Version: 0.3.0.0 } ` followed by `TaskDialog "Revit cannot run the external application "…". Contact the provider for assistance. …"` (R2024) |
| R2015 form | `' 0:< API_SUCCESS { API External Application added: OctaneRender For Revit, Class: OctaneRenderPlugin.Application, Assembly: OctaneRender_for_Revit.dll, VendorId: Otoy Inc, Description: Otoy, www.otoy.com. } ` |
| Ribbon button | `' 0:< API_SUCCESS { Added pushbutton Id: 6404, name: , text: Dynamo 1.3, class: Dynamo.Applications.DynamoRevit, assembly: C:\Program Files\Dynamo\…\DynamoRevitDS.dll } `; ≤R2015: `' 0:< Added new API pushbutton 6403 name  text OctaneRender class OctaneRenderPlugin.Command assembly C:\…\OctaneRender_for_Revit.dll ` |
| Event subscription | `' 0:< API_SUCCESS { Registering Idling event by application Rhino.Inside (02eff7f0-4921-4fd3-91f6-a87b6ba9bf74). } ` / ≤R2015 `' 0:< API registering DocumentOpened event by application OctaneRender For Revit (cc275222-…). ` |
| Command override | `' 0:< API_SUCCESS { Replacing command id 'ID_PHOTO_RENDER_IN_CLOUD' Executed implementation with implementation from application 'RaaS' dll 'C:\…\RaaSApplication.dll'. } ` |
| External service/server | `API_SUCCESS { Registered an external service: Name(…); VendorId(ADSK); ServiceId(…) }`, `An external server has been registered.: …` |
| **Assembly conflict** | `' 1:< API_ERROR { : Assembly version conflict in some references in Microsoft.WindowsAPICodePack.Shell assembly` + lines `'Addin's module mscorlib of version 2.0.0.0 conflicts with same preloaded module of version 4.0.0.0` … `}`. **This is common and usually harmless.** R2024 logs ~15 of them for Autodesk's own add-ins (Precast, ResultsBuilder…). Do not treat it as a failure by itself. |
| Manifest problems | `'C …;  DBG_INFO: The addin file -Dynamo.addin- in current user folder is duplicated.: line 466 of …AddInManagerUtils.cpp.` (R2024); `' 1:< External Command Registration Exception: Invalid long description format.` (R2020) |

Journalysis groups all of these per assembly into a `LoadedAssembly` (name, class, vendor, path, GUID, version) [P].

### 8.2 Add-in exceptions [V]

```
'C 15-May-2020 08:12:39.700;   1:< Managed exception caught from external API application 'RhinoCommon' in method 'IntPtr ON_StringArray_New()' Exception type: '<System.DllNotFoundException>,' Exception method: '<Unable to load DLL 'rhcommon_c': …>,' Stack trace '      at UnsafeNativeMethods.ON_StringArray_New()
' 0:< Managed exception occurred: 
'C 30-Apr-2019 10:34:12.456;   0:< Collection was modified; enumeration operation may not execute.
'WPF RenderMode at time of exception was: HardwareAccelerated
```
Parser-derived [P Journalysis `RecordedExceptionAnalyzer`]: `API_ERROR { An external service execution throws …}`; `API_ERROR { System.<Type> exception(<msg>) was thrown from a handler of <Event> event. The API event handler was registered by application <Name> (<guid>). Changes made by this handler are going to be discarded. }`; `Exception caught from managed method <…> <…>`; `ApplicationException is being thrown on behalf of the function <…>`; `ArchiveException`; `OpenStream FileException`; `exceptionsInOnOpenDocument`; `CLARITY: First Chance Exception: `; `ExceptionProxy:`. Journalysis ignores `ExceptionPolicy`, `Exception occurred` and `unhandledExceptionFilter` when collecting exceptions.

### 8.3 Add-in execution and time

* User launch: `Jrn.RibbonEvent "Execute external command:<ControlId>:<FullClassName>"` [V]. Dynamo: `Jrn.Command "Ribbon"  , "Launch Dynamo , ID_VISUAL_PROGRAMMING_DYNAMO"` [V].
* Duration: `API External Command Time` line(s) after it (§2.3) [V].
* Add-in transactions: `Jrn.Data "Transaction Successful" , "<transaction name>"` [V].
* `Jrn.AddInEvent "AddInJournaling" , "…"` records an add-in's own journaling (e.g. Dynamo button clicks) [V].
* Startup cost of each add-in [I]: the timestamp gap around each `Starting External Application` group. R2024 writes few timestamps there, so the estimate is rough. amcgoey's tool claims to compute it but does not document how.

---

## 9. Crash / termination detection

### 9.1 Normal exit [V]

R2016 (S-2016), in order: `Jrn.Command "SystemMenu" , "Quit the application; prompts to save projects , ID_APP_EXIT"` → `RAM Statistics` ×N → `' 0:< Before : Destroy Display Manager ` / `' 0:< After : Destroy Display Manager ` → `'C …; 0:< ->desktop ExitManagedInstance` → `<-desktop ExitManagedInstance` → `->desktop ExitNativeInstance` → `API_SUCCESS { Unregistering … }` ×N → `' 0:< Unregistering all external services. ` → … → last lines:
```
'C 05-Oct-2015 02:18:24.510;   0:< <-desktop ExitNativeInstance 
' 0:< ::9:: Delta VM: Avail +13 -> 8386998 MB, Used 326 MB; RAM: Avail +0 -> 3660 MB, Used 465 MB 
'C 05-Oct-2015 02:18:24.511;   0:< finished recording journal file 
```
R2024 (S-2024b):
```
'C 21-Oct-2026 18:53:50.395;  <-desktop ExitNativeInstance 
'C 21-Oct-2026 18:53:50.395;  finished recording journal file 
```
R2019 worker log: `' 0:< Journal Exit ` / `' 0:< BC: 0,0,0 ` / `'C …;   0:< finished recording journal file`. R2010: `'C …; 0:< <-ExitInstance` then `finished recording journal file`.

**Primary rule [P Journalysis `WasSessionTerminatedProperly`, V]:** a session ended properly if a timestamp line's description equals `finished recording journal file`. Exit can also be started by closing the window (no `ID_APP_EXIT` command), so don't require that command.

### 9.2 Crash patterns [V]

**A. Hard crash, journal just stops** (R2015, OTOY add-in):
```
 Jrn.RibbonEvent "Execute external command:41d34ec8-525a-4147-bde0-076f05a1b93f:OctaneRenderPlugin.Command"
' 0:< Exception occurred 
'C 18-Dec-2014 13:23:56.340;   0:< ExceptionCode=0xc0000005 ExceptionFlags=0x00000000 ExceptionAddress=000000004C3CA180 
' 0:<   System (MB) [Available /  Total ]  [Revit Memory Usage (MB)   ] 
' 0:< RAM Statistics:    28944 /    32765       412=InUse      424=Peak  
' 0:< VM  Statistics:  8386854 /  8388607       395=InUse      407=Peak  
' 0:< unhandledExceptionFilter is executed in a non-main thread (id = 4988, tag = <blank> : [was ]).
<EOF>
```
**B. Fatal error with recovery prompt, ends with "program terminated"** (R2017):
```
'  1.135326!!! 2:!!!BIG_GAP API External Command Time 
' 1:< Exception occurred 
'C 02-Feb-2017 12:26:58.270;   1:< ExceptionCode=0xc0000005 ExceptionFlags=0x00000000 ExceptionAddress=000000000060A5AF 
' 1:< RAM Statistics:     8515 /    16341      1184=InUse     1209=Peak  
' 1:< Unconverted MessageBox "A fatal error has occurred.  The application will be terminated.  You have the opportunity to save recovery files for all of your changed projects. 
 ' [Jrn.AutoConvertedMessageBox] Rvt.Attr.MessageId: IDS_UNRECOVERABLE_ERROR Rvt.Attr.MessageAnswer: IDNO 
'C 02-Feb-2017 12:27:06.163;   1:< program terminated 
```
**C. Managed (.NET) exception, then shutdown of services** (R2019, Dynamo): `ExceptionCode=0xe0434352 ExceptionFlags=0x00000001` (the CLR exception code), followed by `Revit worker 1 closed cleanly`, `logging finished worker services`, `Terminating Slave Processes`, `unhandledExceptionFilter is executed in a non-main thread …`. These terminations write many "clean" looking lines but still have **no** `finished recording journal file`.

Detection algorithm (recommended):
1. `ended_properly` = there is a timestamp line with description `finished recording journal file`.
2. If not ended properly **and** the file is not the active one (it is not the newest journal, or mtime is older than a few minutes, or a newer journal exists), then **abnormal termination**.
3. Strengthen and classify using the last ~200 lines:
   * `ExceptionCode=0x…` → crash. `0xc0000005` = access violation (native), `0xe0434352` = unhandled .NET exception [I; standard Windows/CLR codes], `0xc0000017`/`0xc000012d` would suggest out-of-memory/commit [I, unverified in journals].
   * `IDS_UNRECOVERABLE_ERROR`, `Unconverted MessageBox "A fatal error has occurred`, `An unrecoverable error has occurred`, `program terminated` → fatal error.
   * `Exception occurred`, `unhandledExceptionFilter` → crash in progress.
   * None of the above and the last line looks ordinary → **killed or power loss / hang killed by the user** (amcgoey's "UNCERTAIN").
   * A matching `journal.NNNN*.dmp` beside it means a crash dump was written.
4. Context to report: the last `Jrn.Command` / `Jrn.RibbonEvent` / `Jrn.AddInEvent` (the last user action), the last memory readings, the open model (last BasicFileInfo), and the time from the last user action to the end.
5. Playback flags [P]: `started journal file playback of "<path>"` (V in R2024), `finished journal file playback` (V), and `stopped at line … journal file playback` (P), which means playback was interrupted.

### 9.3 CER (Customer Error Reporting)

**I found no verbatim CER line in any journal sample.** What is known: after a crash Revit shows the Autodesk error-report dialog, and the CER package includes the journal. Autodesk staff ask users for the CER number (reported as starting with `CER_`) plus the journal from the crashed session. CER can be disabled in Revit.ini (a forum anecdote for R2025.4). An analyzer should therefore **not** expect CER markers in the journal. Look for `.dmp` files and the termination patterns above instead. **Need real crash samples from R2022+ to confirm whether any `CER`/`Dump` path line exists.**

---

## 10. Differences between versions (2010 → 2024 seen; 2025/2026 unknown)

| Feature | ≤R2010 | R2015–R2016 | R2017–R2020 | R2024 (only newer sample) |
|---|---|---|---|---|
| First line | `Initial VM measure: X MB available, Y MB used` | `Initial VM: Avail … ; RAM: …` | same + `GUI Resource Usage` line | same |
| Start line | `'C ts;   0:< started recording journal file` | same | same | `'C ts;  started recording journal file` (no `0:<`) |
| Header | `' Build:` only | `' Build:`, `' Branch:` | + `' Release: 2019.2.2` (seen R2019) | `' Build:`, `' Branch: RELEASE_2024.3.4`, `' Release: 2024.3.4` |
| Memory delta | `VM Delta: Available …` | `Delta VM: … ; RAM: …` | same + GDI line after each | same |
| `Jrn.Directive` | key on 1st line, values on `,` line | same | same | `Jrn.Directive  _` then `"Key"  , values` |
| Add-in load | `Added new API pushbutton …` | R2015 `API External Application added:`; R2016 `Starting External Application:` | `API_SUCCESS { Starting External Application … }` | + `,  Assembly Version: x` |
| WMI OS/CPU/GPU dump | yes | yes (R2015) / not seen (R2016 automation) | not seen | not seen |
| `VIDEO CARD ENVIRONMENT` | no | yes | yes | yes |
| Cloud | — | — | `ModelPath Created … skyscraper` (BIM 360) | `[Jrn.CloudModelCache]`, `[Cloud]openOntoDesktop` |
| Command line spacing | `"Ribbon" , "…"` | same | same; R2022 may add doubled quotes on ID [P Journalysis note "Allow for different formatting in Revit 2022"] | `"Ribbon"  , "…"` |

Parser advice: normalize runs of whitespace, join continuation lines first, then classify. Use substring anchors (`started recording journal file`, `Delta VM:`, `RAM Statistics:`, `API_SUCCESS {`, `API_ERROR {`, `[Jrn.BasicFileInfo]`, `:< SLOG $`) rather than fixed column positions. jeffreyclu's `split(" ")[n]` approach breaks across versions.

---

## 11. Privacy: lines to anonymize

| Data | Lines |
|---|---|
| Windows user name (inside paths) | `this journal = C:\Users\<user>\…`; any `C:\Users\<user>\…` path (add-in assemblies under `AppData\Roaming\Autodesk\Revit\Addins`, model paths); `Connecting session … to Worksharing Log [C:\Users\…]` |
| Revit / Autodesk ID user name | `Jrn.Directive "Username" , "<id>"` (value on the same or next line); `Rvt.Attr.Username:` / `Rvt.Attr.UserName:` in BasicFileInfo; SLOG `user="…"`; cloud `User's Name <id>` |
| Machine name / IPs | `Additional IP address/name found for host <HOST>: <IPv4/IPv6>`; `User's Machine Name <HOST>`; SLOG `host=<ip> "<HOST>"` |
| Servers / shares | `Rvt.Attr.CentralModelPath: \\SERVER\SHARE\…`, `LastSavePath:`, `createNewLocal: … from "J:\…"`, SLOG `server=`, `central=`, `Central server = "https://…"`, `RSN://server/…` |
| Project / client names | model file names and folders in every path above; `Jrn.Activate "[<model>.rvt]" , "Floor Plan: …"`; `Jrn.Directive "WindowSize" , "[<model>]"…`; `Jrn.Data "File Name"`, `"MRUFileName"`; view and sheet names (`Sheet: 1104 - Grundriss Obergeschoss 2`); `TaskDialogResult` texts quoting file names; cloud `CLD:\\{guid}<ProjectName>\…`; `Fix Missing Element For <path>` |
| Printer names / network ports | `PRINTER INFORMATION:` block (`DeviceID`, `PortName` can be `\\printserver\…`) |
| License | `License mode`, `Expiration` (low sensitivity) |
| Free text typed by user | `Jrn.Edit … "ReplaceContents" , "<text>"` (e.g. sync comments), `EditRichText` |
| Not personal | Autodesk build-tree paths (`F:\Ship24.3.4\2024_px64\…`, `d:\ship\2020_px64\…`), `C:\Program Files\Autodesk\…`, GUIDs of Autodesk add-ins |

Anonymization approach: regex-replace `C:\\Users\\[^\\]+` → `C:\Users\<USER>`; UNC `\\\\[^\\]+\\[^\\]+` → `\\<SERVER>\<SHARE>`; drive-letter project paths → keep only the file extension, or hash the name; IPv4/IPv6 → `<IP>`; the host name captured from the `Additional IP address/name found for host X` line, replaced globally; the Username directive value, replaced globally (it also occurs inside paths sometimes).

---

## 12. Still unknown: needs real sample journals

1. R2025 and R2026 journals (the .NET 8 era): header, add-in load line format, any CER/dump reference, and whether `finished recording journal file` is unchanged.
2. A **Ukrainian-locale** journal: month abbreviations in timestamps, encoding of Cyrillic paths and user names (ANSI cp1251 vs UTF-8), and localized command descriptions.
3. A real **Synchronize with Central** in a journal: the exact `Jrn.Command` line, the in-journal SLOG `>STC`/`<STC`, and the TaskDialogs on failure.
4. A crash caused by **out of memory** (what the last memory lines and the ExceptionCode look like), and the "low memory" warning dialog text if any.
5. Whether the WMI OS/CPU block still exists in R2022+ interactive sessions, or whether it moved to the worker log.
6. Verbatim `Error posted`, `A serious error has occurred`, `Jrn.Data "Error dialog"` and `DBG_ERROR` lines in modern versions.
7. How an add-in **manifest error** (bad .addin XML, missing DLL) is logged, beyond the `API_ERROR { Starting External Application … }` + TaskDialog pair seen in R2024.
8. The exact meaning of `'C/'E/'H` and `N:<` (inferred only).
