using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using RevitJournal.Core;
using RevitJournal.Core.Analysis;
using RevitJournal.Core.Reporting;

namespace RevitJournal.App;

public partial class MainWindow : Window
{
    private readonly Analyzer _analyzer = Analyzer.CreateDefault();
    private readonly ObservableCollection<JournalItem> _items = [];
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private CancellationTokenSource? _scan;

    public MainWindow()
    {
        InitializeComponent();
        Sessions.ItemsSource = _items;
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            // "Open with" / drag onto the exe: analyze the given journals first.
            foreach (string path in Environment.GetCommandLineArgs().Skip(1).Where(File.Exists))
                await OpenExternalAsync(path);
        };
        _liveTimer.Tick += async (_, _) => await RefreshChangedAsync();
        _liveTimer.Start();
    }

    /// <summary>Lists every journal on this PC and analyzes them in the background, newest first.</summary>
    private async Task ReloadAsync()
    {
        _scan?.Cancel();
        var cts = _scan = new CancellationTokenSource();

        var files = await Task.Run(() => JournalLocator.FindAll());
        var external = _items.Where(i => string.IsNullOrEmpty(i.File.RevitVersion)).ToList(); // files opened by hand
        _items.Clear();
        foreach (var item in external)
            _items.Add(item);
        foreach (var f in files)
            _items.Add(new JournalItem(f));

        if (_items.Count == 0)
        {
            StatusLine.Text = $"No Revit journals found in {JournalLocator.DefaultRoot}. Use \"Open journal…\" or drag a file here.";
            return;
        }
        if (Sessions.SelectedIndex < 0)
            Sessions.SelectedIndex = 0;

        var pending = _items.Where(i => i.Result is null).ToList();
        int done = 0;
        StatusLine.Text = $"Analyzing {pending.Count} journals…";
        try
        {
            await Parallel.ForEachAsync(pending, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token },
                async (item, ct) =>
                {
                    await AnalyzeAsync(item);
                    int n = Interlocked.Increment(ref done);
                    await Dispatcher.InvokeAsync(() => StatusLine.Text = $"Analyzed {n} of {pending.Count} journals");
                });
            int problems = _items.Count(i => i.StatusKey is "crash" or "warning");
            StatusLine.Text = $"{_items.Count} journals · {problems} with problems or abnormal endings";
        }
        catch (OperationCanceledException)
        {
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
                ? "Select a session on the left, or drag a journal file (journal.NNNN.txt) onto this window."
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
            _items.Insert(0, item);
        }
        Sessions.SelectedItem = item;
        Sessions.ScrollIntoView(item);
        if (item.Result is null)
            await AnalyzeAsync(item);
    }

    private void OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ShowReport(Sessions.SelectedItem as JournalItem);

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
