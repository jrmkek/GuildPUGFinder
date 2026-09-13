using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace GuildPUGFinderApp;

// Wraps a CandidateRow with a display-ready per-boss string, since
// DataGrid columns need a simple bindable property, not a List<BossParse>.
public class CandidateRowView
{
    public string Name { get; }
    public CandidateStatus Status { get; }
    public double? OverallParsePercent { get; }
    public double? DpsParsePercent { get; }
    public double? HealParsePercent { get; }
    public double? TankParsePercent { get; }
    public double? AverageItemLevel { get; }
    public string? Spec { get; }
    public string? ClassName { get; }
    public string PerBossText { get; }
    public string? ErrorMessage { get; }

    public CandidateRowView(CandidateRow row)
    {
        Name = row.Name;
        Status = row.Status;
        OverallParsePercent = row.OverallParsePercent;
        DpsParsePercent = row.DpsParsePercent;
        HealParsePercent = row.HealParsePercent;
        TankParsePercent = row.TankParsePercent;
        AverageItemLevel = row.AverageItemLevel;
        Spec = row.Spec;
        ClassName = row.ClassName;
        PerBossText = string.Join(", ", row.PerBoss.Select(b => $"{b.EncounterName}: {b.RankPercent:F0}%"));
        ErrorMessage = row.ErrorMessage;
    }
}

public class ZoneOption
{
    public int? Id { get; }
    public string Name { get; }
    public ZoneOption(int? id, string name) { Id = id; Name = name; }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CandidateRowView> _rows = new();
    private readonly ObservableCollection<ZoneOption> _zoneOptions = new() { new ZoneOption(null, "Latest tier (default)") };
    private readonly HashSet<string> _seenClasses = new();
    private readonly Dictionary<string, DateTime> _blacklist = new(StringComparer.OrdinalIgnoreCase);
    private Config? _config;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounceTimer;
    private bool _isRunning;
    private string? _lastSeenContent;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        var copyMenu = new System.Windows.Controls.ContextMenu();
        var copyNamesItem = new System.Windows.Controls.MenuItem { Header = "Copy name(s)" };
        copyNamesItem.Click += CopySelectedNames_Click;
        copyMenu.Items.Add(copyNamesItem);
        ResultsGrid.ContextMenu = copyMenu;
        RaidTierCombo.ItemsSource = _zoneOptions;
        RaidTierCombo.SelectedIndex = 0;
        LoadConfig();
        _ = LoadZoneOptionsAsync(); // fire-and-forget, non-blocking

        // Debounce: WoW's SavedVariables write (plus antivirus/OS file
        // events) can fire several Changed events for one actual save, and
        // the file can be briefly locked right as it's written. Wait for
        // things to go quiet for a beat before reading it.
        _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await TryAutoRunAsync();
        };
    }

    private void LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(path))
            path = Path.Combine(Directory.GetCurrentDirectory(), "config.json");

        if (!File.Exists(path))
        {
            StatusText.Text = "config.json not found next to the app.";
            MessageBox.Show(this,
                "config.json wasn't found next to the app.\n\n" +
                "Copy config.example.json to config.json in this same folder, then fill in your " +
                "WarcraftLogs Client ID/Secret, realm, and SavedVariables path.\n\n" +
                "Nothing will work (Run, Watch, List Raid Tiers) until this exists.",
                "Missing config.json", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path));
            if (_config == null || string.IsNullOrWhiteSpace(_config.ClientId) || _config.ClientId.StartsWith("PASTE_"))
            {
                StatusText.Text = "config.json found but credentials look unfilled.";
                MessageBox.Show(this,
                    "config.json was found, but ClientId still looks like the placeholder value.\n\n" +
                    "Open config.json and paste in your real WarcraftLogs Client ID and Secret.",
                    "config.json not filled in", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StatusText.Text = "Config loaded. Ready.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to read config.json: {ex.Message}";
            MessageBox.Show(this, $"config.json exists but couldn't be parsed:\n\n{ex.Message}",
                "Invalid config.json", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadZoneOptionsAsync()
    {
        // worldData.zones (the documented approach) returns a DIFFERENT id
        // space than what zoneRankings(zoneID:) actually expects on this
        // Fresh site - confirmed by empirical probing (see chat history):
        // worldData.zones said "BT/Hyjal" = 1011, but zoneRankings only
        // returns real data for that tier at id 1060. So instead of trusting
        // the live "official" lookup, use ids confirmed by directly probing
        // zoneRankings and matching boss lists against known raid rosters.
        _zoneOptions.Add(new ZoneOption(1047, "Karazhan"));
        _zoneOptions.Add(new ZoneOption(1048, "Gruul's Lair + Magtheridon's Lair (P1)"));
        _zoneOptions.Add(new ZoneOption(1056, "Serpentshrine Cavern + Tempest Keep (P2)"));
        _zoneOptions.Add(new ZoneOption(1060, "Black Temple + Mount Hyjal (P3/current)"));
        await Task.CompletedTask;
    }

    private void ShowPerBossCheck_Changed(object sender, RoutedEventArgs e)
    {
        PerBossColumn.Visibility = ShowPerBossCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_watcher != null)
        {
            StopWatching();
            return;
        }

        if (_config == null || string.IsNullOrWhiteSpace(_config.SavedVariablesPath))
        {
            StatusText.Text = "No valid config.json / SavedVariables path - fix that first.";
            return;
        }

        var dir = Path.GetDirectoryName(_config.SavedVariablesPath);
        if (dir == null || !Directory.Exists(dir))
        {
            StatusText.Text = $"SavedVariables folder not found: {dir}";
            return;
        }

        _watcher = new FileSystemWatcher(dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };

        int eventCount = 0;
        void OnFileEvent(object? _, FileSystemEventArgs args)
        {
            // Directory-wide watch catches a possible temp-file-then-rename
            // write pattern, but that means OTHER addons' SavedVariables
            // writes on every /reload fire events here too. Only react to
            // events actually about our file.
            var eventName = args is RenamedEventArgs renamed ? renamed.Name : args.Name;
            if (eventName == null || !eventName.Equals(Path.GetFileName(_config!.SavedVariablesPath), StringComparison.OrdinalIgnoreCase))
                return;

            Dispatcher.Invoke(() =>
            {
                eventCount++;
                StatusText.Text = $"Watching... ({eventCount} change(s) detected, waiting for quiet)";
                _debounceTimer.Stop();
                _debounceTimer.Start();
            });
        }

        // Some writers (possibly including WoW's SavedVariables save)
        // write to a temp file and rename/replace over the original rather
        // than editing in place - that raises Renamed/Created, not Changed.
        // Listen for all three so we don't miss it either way.
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, args) => Dispatcher.Invoke(() =>
            StatusText.Text = $"Watcher error: {args.GetException()?.Message}");

        WatchButton.Content = "Stop Watching";
        StatusText.Text = "Watching for SavedVariables changes. /pugscan + /reload in-game to trigger a run.";
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounceTimer.Stop();
        WatchButton.Content = "Start Watching";
        StatusText.Text = "Stopped watching.";
    }

    private async Task TryAutoRunAsync()
    {
        if (_isRunning) return; // don't overlap runs if changes arrive mid-run

        if (_config != null && File.Exists(_config.SavedVariablesPath))
        {
            string? content = null;
            // The file can be briefly locked right after WoW finishes
            // writing it - a couple of quick retries is enough.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { content = File.ReadAllText(_config.SavedVariablesPath); break; }
                catch (IOException) { await Task.Delay(300); }
            }

            if (content != null && content == _lastSeenContent)
            {
                StatusText.Text = "Watching... (file changed but content is identical to last run, skipping)";
                return;
            }
            _lastSeenContent = content;
        }

        var options = BuildOptionsFromUi(out var error);
        if (options == null)
        {
            StatusText.Text = $"Auto-run skipped: {error}";
            return;
        }
        await RunPipelineAsync(options);
    }

    private RunOptions? BuildOptionsFromUi(out string? error)
    {
        error = null;

        if (!double.TryParse(MinParseBox.Text, out var minParse) ||
            !double.TryParse(MinIlvlBox.Text, out var minIlvl) ||
            !double.TryParse(MinPerBossBox.Text, out var minPerBoss))
        {
            error = "Min parse / ilvl / per-boss fields need to be numbers.";
            return null;
        }

        var allowedClasses = new HashSet<string>();
        foreach (var (box, name) in new (System.Windows.Controls.CheckBox, string)[]
        {
            (ClassWarrior, "Warrior"), (ClassPaladin, "Paladin"), (ClassHunter, "Hunter"),
            (ClassRogue, "Rogue"), (ClassPriest, "Priest"), (ClassShaman, "Shaman"),
            (ClassMage, "Mage"), (ClassWarlock, "Warlock"), (ClassDruid, "Druid"),
        })
        {
            if (box.IsChecked == true) allowedClasses.Add(name);
        }

        var selectedZone = RaidTierCombo.SelectedItem as ZoneOption;

        bool queryOverall = QueryOverallCheck.IsChecked == true;
        bool queryDps = QueryDpsCheck.IsChecked == true;
        bool queryHeal = QueryHealCheck.IsChecked == true;
        bool queryTank = QueryTankCheck.IsChecked == true;

        if (!queryOverall && !queryDps && !queryHeal && !queryTank)
        {
            error = "Check at least one parse category to query.";
            return null;
        }

        return new RunOptions
        {
            MinBestParsePercent = minParse,
            MinAverageItemLevel = minIlvl,
            AllowedClasses = allowedClasses,
            UsePerBossParse = UsePerBossCheck.IsChecked == true,
            MinPerBossParsePercent = minPerBoss,
            ZoneId = selectedZone?.Id,
            QueryOverall = queryOverall,
            QueryDps = queryDps,
            QueryHeal = queryHeal,
            QueryTank = queryTank,
            BlacklistedNames = GetActiveBlacklist(),
        };
    }

    private static readonly TimeSpan BlacklistTtl = TimeSpan.FromDays(1);

    // Removes entries older than 24h and returns the still-active names.
    // Called right before every run so expiry is always up to date.
    private HashSet<string> GetActiveBlacklist()
    {
        var expired = _blacklist.Where(kv => DateTime.UtcNow - kv.Value > BlacklistTtl).Select(kv => kv.Key).ToList();
        foreach (var name in expired) _blacklist.Remove(name);
        return new HashSet<string>(_blacklist.Keys, StringComparer.OrdinalIgnoreCase);
    }

    private void BlacklistSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsGrid.SelectedItems.Cast<CandidateRowView>().ToList();
        if (selected.Count == 0) return;

        foreach (var row in selected)
        {
            _blacklist[row.Name] = DateTime.UtcNow;
            _rows.Remove(row);
        }
        StatusText.Text = $"Blacklisted {selected.Count} name(s) for 24h. They'll be skipped on every run until then.";
    }

    private void ViewBlacklistButton_Click(object sender, RoutedEventArgs e)
    {
        GetActiveBlacklist(); // prune expired before showing
        var window = new BlacklistWindow(_blacklist) { Owner = this };
        window.ShowDialog();
    }

    private void ResultsGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source is not System.Windows.Controls.DataGridRow)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);

        if (source is not System.Windows.Controls.DataGridRow row || row.Item is not CandidateRowView)
            return;

        if (!row.IsSelected)
        {
            ResultsGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    private void CopySelectedNames_Click(object sender, RoutedEventArgs e)
    {
        var names = ResultsGrid.SelectedItems
            .Cast<CandidateRowView>()
            .Select(row => row.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        if (names.Count == 0) return;

        Clipboard.SetText(string.Join(Environment.NewLine, names));
        StatusText.Text = $"Copied {names.Count} name(s) to the clipboard.";
    }

   

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptionsFromUi(out var error);
        if (options == null)
        {
            _rows.Clear();
            StatusText.Text = error;
            return;
        }
        await RunPipelineAsync(options, forceFresh: true);
    }

    private async Task RunPipelineAsync(RunOptions options, bool forceFresh = false)
    {
        if (_config == null)
        {
            StatusText.Text = "No valid config.json loaded - fix that first.";
            return;
        }

        _isRunning = true;
        _rows.Clear();
        _seenClasses.Clear();
        RunButton.IsEnabled = false;
        StatusText.Text = "Running...";

        try
        {
            var runner = new PipelineRunner(_config);
            var results = await runner.RunAsync(options, row =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (row.ClassName != null) _seenClasses.Add(row.ClassName);
                    SeenClassesText.Text = string.Join(", ", _seenClasses.OrderBy(c => c));

                    bool matchesClassFilter = options.AllowedClasses.Count == 0
                        || (row.ClassName != null && options.AllowedClasses.Contains(row.ClassName));

                    if (matchesClassFilter)
                        _rows.Add(new CandidateRowView(row));
                });
            }, forceFresh);

            int passCount = results.Count(r => r.Status == CandidateStatus.Pass);
            StatusText.Text = $"Done. {passCount} of {results.Count} eligible. Written back to SavedVariables - /reload in-game to pick them up.";

            // The run just wrote results back into the SAME file we're
            // watching, which will fire another file-change event on its
            // own. Update our tracked content to match what we just wrote,
            // so that self-triggered event gets recognized as "nothing
            // new" and skipped instead of causing a redundant second run.
            try { _lastSeenContent = File.ReadAllText(_config.SavedVariablesPath); }
            catch { /* best-effort - worst case one extra redundant run happens */ }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            RunButton.IsEnabled = true;
            _isRunning = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _watcher?.Dispose();
        base.OnClosed(e);
    }
}
