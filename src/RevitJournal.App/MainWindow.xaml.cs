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
using RevitJournal.Core.Reporting;
using RevitJournal.Core.Symptoms;

namespace RevitJournal.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly Analyzer _analyzer = Analyzer.CreateDefault();
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
        Title = $"Revit Journal Analyzer {AppInfo.Version}";
        _advisor = SymptomAdvisor.CreateDefault(_analyzer.Rules);
        SymptomCards.ItemsSource = _advisor.Symptoms.Select(s => new SymptomCard(s)).ToList();

        Sessions.ItemsSource = _items;
        var view = CollectionViewSource.GetDefaultView(_items);
        view.SortDescriptions.Add(new SortDescription(nameof(JournalItem.SortKey), ListSortDirection.Descending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(JournalItem.Day)));
        view.Filter = o => o is JournalItem i && MatchesVersion(i);

        Loaded += async (_, _) =>
        {
            await ReloadAsync();
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
                agent.ShowInfo("Still watching Revit", "Revit Journal Analyzer keeps running here and will warn you if Revit crashes, stops responding or cannot sync.");
                settings.TrayHintShown = true;
                settings.Save();
            }
        };
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
                Text = "Checking the Revit journals on this computer…", Margin = new Thickness(40, 32, 40, 0), FontSize = 16,
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
        string which = SelectedVersion is { } v ? $"Revit {v}" : "all Revit versions";
        HomeSubtitle.Text = $"Pick what you see. We'll check the journals of {which} on this computer.";
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
            int problems = _items.Count(i => i.StatusKey is "crash" or "warning");
            StatusLine.Text = $"{_items.Count} journals · {problems} with problems or abnormal endings";
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
            var result = await Task.Run(() => _analyzer.AnalyzeFile(item.File.Path));
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

    /// <summary>Re-analyzes journals that Revit is still writing, so a running or hung session stays current.</summary>
    private async Task RefreshChangedAsync()
    {
        foreach (var item in _items.ToList())
        {
            var info = new FileInfo(item.File.Path);
            if (!info.Exists || (info.LastWriteTime == item.File.LastWrite && info.Length == item.File.SizeBytes
                                 && item.Result?.Status != SessionStatus.Running))
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
        await ReloadAsync();
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
