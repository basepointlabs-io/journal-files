using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Live;
using RevitJournal.Core.Reporting;
using RevitJournal.Core.Symptoms;

namespace RevitJournal.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Analyzer _analyzer = Analyzer.CreateDefault();
    private bool _rereadAll;
    private readonly SymptomAdvisor _advisor;
    private readonly ObservableCollection<JournalItem> _items = [];
    private readonly Dictionary<DateOnly, DayGroup> _days = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _scan;
    private TaskCompletionSource _analyzed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Symptom? _shownSymptom;
    private Hint? _hint;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = AppInfo.Version;
        Title = $"Revit Doctor {AppInfo.Version}";
        _advisor = SymptomAdvisor.CreateDefault(_analyzer.Rules);

        Sessions.ItemsSource = _items;
        var view = CollectionViewSource.GetDefaultView(_items);
        view.SortDescriptions.Add(new SortDescription(nameof(JournalItem.SortKey), ListSortDirection.Descending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(JournalItem.Day)));
        view.Filter = o => o is JournalItem i && MatchesVersion(i);

        // Finished sessions are analyzed once and then read from %LOCALAPPDATA%\RevitDoctor\cache.
        _analyzer.Cache = new AnalysisCache(Settings.CacheFolder, _analyzer.Rules);
        _analyzer.DiskHistory = RevitJournal.Core.Live.DiskHistory.Default;

        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            _ = Task.Run(() => _analyzer.Cache.Prune());
            // "Open with" / drag onto the exe: analyze the given journals first.
            string[] args = Environment.GetCommandLineArgs()[1..];
            foreach (string path in args.Where(File.Exists))
                await OpenExternalAsync(path);
            // --symptom <id> opens the answer directly (e.g. from a desktop shortcut "Revit is slow").
            int at = Array.IndexOf(args, "--symptom");
            if (at >= 0 && at + 1 < args.Length && _advisor.Symptoms.FirstOrDefault(s => s.Id == args[at + 1]) is { } symptom)
                await ShowDiagnosisAsync(symptom);
        };
        _liveTimer.Tick += async (_, _) => await RefreshChangedAsync();
        _liveTimer.Start();
    }

    public Analyzer Analyzer => _analyzer;

    // ---- live monitoring (tray agent) --------------------------------------------------------------------

    private TrayAgent? _agent;
    private readonly HashSet<string> _livePaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _exiting;

    public void AttachAgent(TrayAgent agent)
    {
        _agent = agent;
        agent.OpenRequested += BringToFront;
        agent.AlertClicked += async alert => await OpenAlertAsync(alert);
        agent.StatusChanged += status =>
        {
            LiveCards.ItemsSource = status.Select(s => new LiveCardView(s, DateTime.Now)).ToList();
            LiveTitle.Visibility = status.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        agent.ExitRequested += () => _exiting = true;
        agent.ResultsChanged += ApplyLiveResults;
        agent.HealthChanged += (report, sessions) =>
        {
            if (_demo)
                return; // the snapshot run is showing made-up numbers
            var view = new HealthView(report, sessions);
            FollowRunningVersions([.. report.Revits.Select(p => p.Version).Where(v => v.Length > 0)]);
            ShowHealth(view);
            HealthCard.Visibility = Visibility.Visible;
            _flyout?.Update(view);
            _lastHealth = view;
        };
        agent.OverviewRequested += ShowOverview;
        // Keep the hint and the history in step with what just happened (a crash, a failed sync).
        agent.AlertsRaised += async alerts =>
        {
            if (!_analyzed.Task.IsCompleted)
                return;
            if (alerts.Any(a => !_items.Any(i => string.Equals(i.File.Path, a.JournalPath, StringComparison.OrdinalIgnoreCase))))
                await ReloadAsync();
            else
                await RefreshChangedAsync();
            UpdateHints();
        };

        // Closing the window keeps the agent watching Revit; Exit in the tray menu really quits.
        Closing += (_, e) =>
        {
            if (_exiting)
                return;
            e.Cancel = true;
            Hide();
            var settings = Settings.Load();
            if (!settings.TrayHintShown)
            {
                agent.ShowInfo("Still watching Revit", "Revit Doctor keeps running here and will warn you if Revit crashes, stops responding or cannot sync.");
                settings.TrayHintShown = true;
                settings.Save();
            }
        };
    }

    /// <summary>
    /// Running sessions are analyzed by the tray agent, which follows their journals line by line; the window shows
    /// those results instead of parsing a growing journal again.
    /// </summary>
    private void ApplyLiveResults(IReadOnlyDictionary<string, AnalysisResult> results)
    {
        foreach (var (path, result) in results)
        {
            var item = _items.FirstOrDefault(i => string.Equals(i.File.Path, path, StringComparison.OrdinalIgnoreCase));
            var info = new FileInfo(path);
            if (!info.Exists)
                continue;
            if (item is null)
            {
                // A Revit session that started after the list was built.
                string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(path)) ?? "");
                string version = folder.StartsWith("Autodesk Revit ", StringComparison.OrdinalIgnoreCase) ? folder["Autodesk Revit ".Length..] : "";
                item = new JournalItem(new JournalFile(path, version, 0, info.LastWriteTime, info.Length));
                Add(item);
                if (VersionPicker.Items.Count == 0)
                    FillVersions();
            }
            if (ReferenceEquals(item.Result, result))
                continue;
            // Remember the size this result stands for, so the periodic check does not read the file again.
            item.Refresh(item.File with { LastWrite = info.LastWriteTime, SizeBytes = info.Length });
            item.Result = result;
            item.Error = null;
            if (result.Status == SessionStatus.Running)
                _livePaths.Add(path);
            else
                _livePaths.Remove(path);
            if (ReferenceEquals(Sessions.SelectedItem, item))
                ShowReport(item);
        }
    }

    private HealthFlyout? _flyout;
    private HealthView? _lastHealth;

    /// <summary>Puts a reading on the start screen and its state beside "Right now" in the list.</summary>
    private void ShowHealth(HealthView view)
    {
        HomeHealth.DataContext = view;
        HomeNavHint.Text = view.StatusTitle;
        HomeNavDot.Fill = (System.Windows.Media.Brush)new StatusBrushConverter().Convert(view.StatusKey, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture);
    }
    private bool _demo;

    /// <summary>
    /// For checking the look of a build without touching the desktop: draws the start screen and the tray overview
    /// into PNG files while both windows stay off-screen and never take the focus (RJA_SNAPSHOT=&lt;folder&gt;).
    /// </summary>
    public async Task SaveSnapshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = 0;
        ShowActivated = false;
        ShowInTaskbar = false;
        Show(); // loading starts when the window is first shown
        await Task.Delay(500);
        await _analyzed.Task;
        for (int i = 0; i < 40 && _lastHealth is null; i++)
            await Task.Delay(500);
        await Task.Delay(12000); // a few readings, so processor shares and the memory curve have values
        SavePng(this, System.IO.Path.Combine(folder, "home.png"));

        var flyout = new HealthFlyout { ShowActivated = false, Left = -32000, Top = 0 };
        if (_lastHealth is not null)
            flyout.Update(_lastHealth);
        flyout.Show();
        await Task.Delay(1000);
        SavePng(flyout, System.IO.Path.Combine(folder, "overview.png"));
        flyout.Close();

        // The same screen with a Revit that has a large model open: made-up numbers, to look at the layout
        // on a computer where no Revit is running.
        var at = DateTime.Now;
        var demo = new HealthView(
            new HealthReport(at, HealthLevel.Watch, 32_212, 9_800, 40_000, 15_000, 20,
                [new RevitHealth(1, "2025", at.AddHours(-2), true, 18_400, 1, 2_150, 900, HealthLevel.Ok),
                 new RevitHealth(2, "2024", at.AddHours(-1), true, 6_200, 9, 1_300, 600, HealthLevel.Ok)],
                [new DiskHealth("C:", 22_500, 476_000, HealthLevel.Watch)],
                [new HealthIssue("disk:C:", HealthLevel.Watch, "Drive C: has only 22.0 GB free",
                    "Autodesk requires at least 30 GB of free disk space for Revit. Free up space: Windows Disk Cleanup, and the cloud model cache you no longer need."),
                 new HealthIssue("models-large", HealthLevel.Watch, "2 open models with their links are 4.8 GB on disk together",
                    "Estimate on Autodesk's Memory Recommendations (RAM about 20 times the file size): about 96.9 GB of RAM; this computer has 31.5 GB. Reduce the number of loaded links, limit the worksets and views you open, and restart Revit regularly. [Autodesk: Memory Usage and Instability]")],
                null, [.. Enumerable.Range(0, 30).Select(i => new RamPoint(at.AddMinutes(i - 30), 14_000 - i * 140))]),
            [new LiveStatus("2025", "", "Tower_ARC.rvt", at, true, "Last sync OK at 10:42:10", 9_800, 32_212, null, 1,
                new RevitJournal.Core.Analysis.ModelLoad("Tower_ARC.rvt", 1_780, 436_899, 13, 13, 2_150, "about 76.8 GB of RAM", true), 9, 1),
             new LiveStatus("2024", "", "Clinic_MEP.rvt", at, true, "Last sync OK at 10:15:02", 9_800, 32_212, null, 2,
                new RevitJournal.Core.Analysis.ModelLoad("Clinic_MEP.rvt", 410, 120_500, 4, 4, 620, "about 20.1 GB of RAM", false), 3, 1)]);
        FollowRunningVersions(["2025", "2024"]);
        _demo = true;
        ShowHealth(demo);
        await Task.Delay(800);
        SavePng(this, System.IO.Path.Combine(folder, "home-demo.png"));
        // The same computer when memory and disk have run low: the figures turn red.
        ShowHealth(new HealthView(
            new HealthReport(at, HealthLevel.Danger, 32_212, 1_100, 40_000, 3_000, 20,
                [new RevitHealth(1, "2025", at.AddHours(-2), true, 26_400, 1, 2_150, 900, HealthLevel.Ok)],
                [new DiskHealth("C:", 3_200, 476_000, HealthLevel.Danger)],
                [new HealthIssue("ram-low", HealthLevel.Danger, "RAM is almost full: 1.1 GB of 31.5 GB free",
                    "Save now. Close the Revit window you are not working in and other heavy programs; Revit can close unexpectedly when memory runs out."),
                 new HealthIssue("disk:C:", HealthLevel.Danger, "Drive C: has only 3.1 GB free",
                    "Free up space now: Revit needs room for temporary files, saves and the cloud model cache, and opening, saving and synchronizing fail when the drive is full. Autodesk requires at least 30 GB free.")],
                TimeSpan.FromMinutes(4), [.. Enumerable.Range(0, 30).Select(i => new RamPoint(at.AddMinutes(i - 30), 9_000 - i * 272))]),
            [new LiveStatus("2025", "", "Tower_ARC.rvt", at, true, "Last sync OK at 10:42:10", 1_100, 32_212, null, 1,
                new RevitJournal.Core.Analysis.ModelLoad("Tower_ARC.rvt", 1_780, 436_899, 13, 13, 2_150, "about 76.8 GB of RAM", true), 14, 1)]));
        await Task.Delay(800);
        SavePng(this, System.IO.Path.Combine(folder, "home-low.png"));

        var demoFlyout = new HealthFlyout { ShowActivated = false, Left = -32000, Top = 0 };
        demoFlyout.Update(demo);
        demoFlyout.Show();
        await Task.Delay(800);
        SavePng(demoFlyout, System.IO.Path.Combine(folder, "overview-demo.png"));
        demoFlyout.Close();
        if (_lastHealth is not null)
            ShowHealth(_lastHealth);
        _demo = false;

        // A report: the session with the most to say.
        if (_items.Where(i => i.Result is not null).OrderByDescending(i => i.Result!.WorstSeverity).ThenByDescending(i => i.Result!.Findings.Count).FirstOrDefault() is { } item)
        {
            ShowReport(item);
            ShowPage(ReportPage);
            await Task.Delay(1500);
            SavePng(this, System.IO.Path.Combine(folder, "report.png"));
        }
    }

    private static void SavePng(FrameworkElement element, string path)
    {
        const double scale = 1.5;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(element.ActualWidth * scale), (int)(element.ActualHeight * scale),
            96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>The compact overview above the tray; "Open full analysis" in it brings up this window.</summary>
    private void ShowOverview()
    {
        if (_flyout is null)
        {
            _flyout = new HealthFlyout();
            _flyout.OpenFullRequested += BringToFront;
        }
        if (_lastHealth is not null)
            _flyout.Update(_lastHealth);
        if (_flyout.IsVisible)
            _flyout.Hide();
        else
            _flyout.ShowNearTray();
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async Task OpenAlertAsync(Core.Live.LiveAlert alert)
    {
        BringToFront();
        // Alerts about the computer (memory, drawing resources, disk) have no journal: the overview on the start screen explains them.
        if (alert.JournalPath.Length == 0)
        {
            ShowPage(HomePage);
            HomePage.ScrollToTop();
            return;
        }
        await _analyzed.Task;
        // The alert may be about a journal that started after the list was built.
        if (!_items.Any(i => string.Equals(i.File.Path, alert.JournalPath, StringComparison.OrdinalIgnoreCase)))
            await ReloadAsync();
        else
            await RefreshChangedAsync();
        if (VersionPicker.ItemsSource is IEnumerable<VersionOption> options
            && options.FirstOrDefault(o => o.Version is { } v && alert.JournalPath.Contains($"Autodesk Revit {v}", StringComparison.OrdinalIgnoreCase)) is { } option)
            VersionPicker.SelectedItem = option;
        if (_advisor.Symptoms.FirstOrDefault(s => s.Id == alert.SymptomId) is { } symptom)
            await ShowDiagnosisAsync(symptom);
    }

    // ---- pages -------------------------------------------------------------------------------------------

    private void ShowPage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { HomePage, AnswerPage, ReportPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        if (page != AnswerPage)
            _shownSymptom = null;
        ReportActions.Visibility = page == ReportPage ? Visibility.Visible : Visibility.Collapsed;
        HomeNav.IsChecked = page == HomePage;
        // Leaving the report clears the selection, so clicking the same session again opens it again.
        if (page != ReportPage)
            Sessions.SelectedItem = null;
    }

    // ---- window buttons (drawn as squares in the title bar, as in the FlexiData Configurator) ------------

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeIcon.Symbol = WindowState == WindowState.Maximized ? Wpf.Ui.Controls.SymbolRegular.SquareMultiple24 : Wpf.Ui.Controls.SymbolRegular.Square24;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    /// <summary>A running Revit in the sidebar opens its live journal in History.</summary>
    private async void OnLiveCardClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LiveCardView card)
            await OpenExternalAsync(card.JournalPath);
    }

    private void OnHome(object sender, RoutedEventArgs e) => ShowPage(HomePage);

    private async void OnSymptomClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SymptomCard card)
            await ShowDiagnosisAsync(card.Symptom);
    }

    private async void OnHintClick(object sender, RoutedEventArgs e)
    {
        if (_hint is not null)
            await ShowDiagnosisAsync(_hint.Symptom);
    }

    private async Task ShowDiagnosisAsync(Symptom symptom)
    {
        ShowPage(AnswerPage);
        _shownSymptom = symptom;
        if (!_analyzed.Task.IsCompleted)
        {
            Answer.Content = new System.Windows.Controls.TextBlock
            {
                Text = "Checking the Revit journals on this computer…", Margin = new Thickness(16, 16, 16, 0), FontSize = 16,
            };
            await _analyzed.Task;
            if (_shownSymptom != symptom)
                return; // the user went elsewhere meanwhile
        }
        // A running Revit may have written more since the last scan; "slow / not responding" needs the latest state.
        await RefreshChangedAsync();
        Answer.Content = new DiagnosisView(_advisor.Diagnose(symptom, VisibleResults(), DateTime.Now));
        AnswerPage.ScrollToTop();
    }

    private void OnOpenFullReport(object sender, RoutedEventArgs e)
    {
        if (Answer.Content is not DiagnosisView { Diagnosis.Session: { } session })
            return;
        var item = _items.FirstOrDefault(i => ReferenceEquals(i.Result, session));
        if (item is null)
            return;
        if (item.Day is { } day)
            day.IsExpanded = true;
        ShowPage(ReportPage);
        Sessions.SelectedItem = item;
        Sessions.ScrollIntoView(item);
    }

    // ---- Revit version -----------------------------------------------------------------------------------

    private string? SelectedVersion => (VersionPicker.SelectedItem as VersionOption)?.Version;

    private bool MatchesVersion(JournalItem i) =>
        SelectedVersion is not { } v || string.IsNullOrEmpty(i.File.RevitVersion) || i.File.RevitVersion == v;

    private List<AnalysisResult> VisibleResults() =>
        [.. _items.Where(i => MatchesVersion(i) && i.Result is not null).Select(i => i.Result!)];

    /// <summary>Several Revit versions can be installed; default to the one used most recently.</summary>
    private void FillVersions()
    {
        var versions = _items.Select(i => i.File.RevitVersion).Where(v => v.Length > 0).Distinct().OrderDescending().ToList();
        string? keep = SelectedVersion;
        string? newest = _items.Where(i => i.File.RevitVersion.Length > 0).MaxBy(i => i.File.LastWrite)?.File.RevitVersion;

        var options = versions.Select(v => new VersionOption(v, $"Revit {v}")).ToList();
        if (versions.Count > 1)
            options.Insert(0, new VersionOption(null, "All versions"));
        VersionPicker.ItemsSource = options;
        VersionPicker.SelectedItem = options.FirstOrDefault(o => o.Version == (keep ?? newest)) ?? options.FirstOrDefault();
        VersionPicker.IsEnabled = options.Count > 1;
    }

    private string _runningVersions = "";

    /// <summary>
    /// The version picker follows what is running: one version selects it, several select "All versions" and are
    /// named beside the picker. It only moves when the set of running versions changes, so a choice made by hand stays.
    /// </summary>
    private void FollowRunningVersions(IReadOnlyList<string> running)
    {
        var groups = running.GroupBy(v => v).OrderBy(g => g.Key).ToList();
        string key = string.Join(",", groups.Select(g => $"{g.Key}x{g.Count()}"));
        if (key == _runningVersions)
            return;
        _runningVersions = key;
        VersionLabel.Text = groups.Count == 0 ? "Revit version"
            : "Running: " + string.Join(", ", groups.Select(g => $"Revit {g.Key}" + (g.Count() > 1 ? $" ×{g.Count()}" : "")));
        if (groups.Count == 0 || VersionPicker.ItemsSource is not IEnumerable<VersionOption> options)
            return;
        var target = groups.Count == 1
            ? options.FirstOrDefault(o => o.Version == groups[0].Key)
            : options.FirstOrDefault(o => o.Version is null);
        if (target is not null && !Equals(VersionPicker.SelectedItem, target))
            VersionPicker.SelectedItem = target;
    }

    private async void OnVersionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        CollectionViewSource.GetDefaultView(_items).Refresh();
        UpdateHomeText();
        if (_analyzed.Task.IsCompleted)
        {
            UpdateHints();
            if (_shownSymptom is { } s)
                await ShowDiagnosisAsync(s);
        }
    }

    private void UpdateHomeText()
    {
        // The start screen no longer has text that depends on the selected version.
    }

    // ---- loading and analysis ----------------------------------------------------------------------------

    /// <summary>Lists every journal on this PC and analyzes them in the background, newest first.</summary>
    private async Task ReloadAsync()
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();
        if (_analyzed.Task.IsCompleted)
            _analyzed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var files = await Task.Run(() => JournalLocator.FindAll());
        var external = _items.Where(i => string.IsNullOrEmpty(i.File.RevitVersion)).ToList(); // files opened by hand
        _items.Clear();
        _days.Clear();
        foreach (var item in external)
            Add(item);
        foreach (var f in files)
            Add(new JournalItem(f));
        FillVersions();
        UpdateHomeText();

        if (_items.Count == 0)
        {
            StatusLine.Text = $"No Revit journals found in {JournalLocator.DefaultRoot}. Use \"Open journal…\" or drag a file here.";
            _analyzed.TrySetResult();
            return;
        }

        var pending = _items.Where(i => i.Result is null).ToList();
        int done = 0;
        StatusLine.Text = $"Checking {pending.Count} journals…";
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token },
                async (item, ct) =>
                {
                    await AnalyzeAsync(item);
                    int n = Interlocked.Increment(ref done);
                    await Dispatcher.InvokeAsync(() => StatusLine.Text = $"Checked {n} of {pending.Count} journals");
                });
            int crashes = _items.Count(i => i.StatusKey == "crash");
            int problems = _items.Count(i => i.StatusKey is "warning" or "abnormal");
            StatusLine.Text = $"{_items.Count} journals · {crashes} crashed · {problems} with problems";
            UpdateHints();
            _analyzed.TrySetResult();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateHints()
    {
        var results = VisibleResults();
        _hint = _advisor.Suggest(results, DateTime.Now);
        HintText.Text = _hint?.Text ?? "";
        HintBanner.Visibility = _hint is null ? Visibility.Collapsed : Visibility.Visible;

        if (History.RepeatedCrashes(_analyzer.Rules, results) is { } f)
        {
            HistoryTitle.Text = f.Rule.Title.En;
            HistoryFacts.ItemsSource = f.Facts;
            HistoryBanner.Visibility = Visibility.Visible;
        }
        else
        {
            HistoryBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async Task AnalyzeAsync(JournalItem item)
    {
        try
        {
            bool fresh = _rereadAll;
            var result = await Task.Run(() => _analyzer.AnalyzeFile(item.File.Path, useCache: !fresh));
            await Dispatcher.InvokeAsync(() =>
            {
                item.Result = result;
                item.Error = null;
                if (ReferenceEquals(Sessions.SelectedItem, item))
                    ShowReport(item);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await Dispatcher.InvokeAsync(() => item.Error = ex.Message);
        }
    }

    /// <summary>Re-analyzes journals whose file changed on disk (e.g. one opened by hand and replaced).</summary>
    private async Task RefreshChangedAsync()
    {
        foreach (var item in _items.ToList())
        {
            // Journals of running sessions come from the tray agent (ApplyLiveResults).
            if (_livePaths.Contains(item.File.Path))
                continue;
            var info = new FileInfo(item.File.Path);
            if (!info.Exists || (info.LastWriteTime == item.File.LastWrite && info.Length == item.File.SizeBytes))
                continue;
            item.Refresh(item.File with { LastWrite = info.LastWriteTime, SizeBytes = info.Length });
            await AnalyzeAsync(item);
        }
    }

    /// <summary>Adds a session to the list and to its day group (one shared DayGroup per date).</summary>
    private void Add(JournalItem item)
    {
        var date = DateOnly.FromDateTime(item.File.LastWrite);
        if (!_days.TryGetValue(date, out var day))
            _days[date] = day = new DayGroup(date);
        day.Items.Add(item);
        item.Day = day;
        day.Recompute();
        _items.Add(item);
    }

    // ---- history page ------------------------------------------------------------------------------------

    private void ShowReport(JournalItem? item)
    {
        if (item?.Result is { } r)
        {
            Report.Content = new ReportView(r);
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            Report.Content = null;
            EmptyState.Visibility = Visibility.Visible;
            EmptyState.Text = item is null
                ? "Open a day on the left and select a session, or drag a journal file (journal.NNNN.txt) onto this window."
                : item.Error ?? "Analyzing…";
        }
    }

    private async Task OpenExternalAsync(string path)
    {
        var info = new FileInfo(path);
        var item = _items.FirstOrDefault(i => string.Equals(i.File.Path, info.FullName, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            item = new JournalItem(new JournalFile(info.FullName, "", 0, info.LastWriteTime, info.Length));
            Add(item);
        }
        if (item.Day is { } day)
            day.IsExpanded = true;
        ShowPage(ReportPage);
        Sessions.SelectedItem = item;
        Sessions.ScrollIntoView(item);
        if (item.Result is null)
            await AnalyzeAsync(item);
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (Sessions.SelectedItem is not JournalItem item)
            return;
        ShowReport(item);
        ShowPage(ReportPage);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.Result = null;
        // Refresh means "read the journals again", not "show the stored results again".
        _rereadAll = true;
        try
        {
            await ReloadAsync();
        }
        finally
        {
            _rereadAll = false;
        }
    }

    private async void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a Revit journal",
            Filter = "Revit journals (journal*.txt)|journal*.txt|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(JournalLocator.DefaultRoot) ? JournalLocator.DefaultRoot : null,
        };
        if (dialog.ShowDialog(this) == true)
            await OpenExternalAsync(dialog.FileName);
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is JournalItem item)
            Process.Start("explorer.exe", $"/select,\"{item.File.Path}\"");
    }

    private void OnCopyReport(object sender, RoutedEventArgs e)
    {
        if (Sessions.SelectedItem is JournalItem { Result: { } r })
        {
            Clipboard.SetText(TextReport.Render(r));
            StatusLine.Text = "Report copied. It contains file paths and names from the journal — check before sharing.";
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            foreach (string f in files.Where(File.Exists))
                await OpenExternalAsync(f);
    }

    private void OnOpenLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
