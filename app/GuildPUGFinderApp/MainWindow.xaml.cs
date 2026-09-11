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
    public double? BestParsePercent { get; }
    public double? AverageItemLevel { get; }
    public string? Spec { get; }
    public string? ClassName { get; }
    public string PerBossText { get; }
    public string? ErrorMessage { get; }

    public CandidateRowView(CandidateRow row)
    {
        Name = row.Name;
        Status = row.Status;
        BestParsePercent = row.BestParsePercent;
        AverageItemLevel = row.AverageItemLevel;
        Spec = row.Spec;
        ClassName = row.ClassName;
        PerBossText = string.Join(", ", row.PerBoss.Select(b => $"{b.EncounterName}: {b.RankPercent:F0}%"));
        ErrorMessage = row.ErrorMessage;
    }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<CandidateRowView> _rows = new();
    private readonly HashSet<string> _seenClasses = new();
    private Config? _config;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounceTimer;
    private bool _isRunning;
    private string? _lastSeenContent;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        LoadConfig();

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

        int? partition = null;
        if (!string.IsNullOrWhiteSpace(PartitionBox.Text))
        {
            if (!int.TryParse(PartitionBox.Text, out var p))
            {
                error = "Partition must be a whole number, or left blank.";
                return null;
            }
            partition = p;
        }

        int? zoneId = null;
        if (!string.IsNullOrWhiteSpace(ZoneIdBox.Text))
        {
            if (!int.TryParse(ZoneIdBox.Text, out var z))
            {
                error = "Zone ID must be a whole number, or left blank.";
                return null;
            }
            zoneId = z;
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

        return new RunOptions
        {
            MinBestParsePercent = minParse,
            MinAverageItemLevel = minIlvl,
            AllowedClasses = allowedClasses,
            UsePerBossParse = UsePerBossCheck.IsChecked == true,
            MinPerBossParsePercent = minPerBoss,
            Partition = partition,
            ZoneId = zoneId,
        };
    }

    private async void ListZonesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_config == null)
        {
            StatusText.Text = "No valid config.json loaded - fix that first.";
            return;
        }

        ListZonesButton.IsEnabled = false;
        StatusText.Text = "Fetching zone list...";
        try
        {
            var client = new WarcraftLogsClient(_config.ClientId, _config.ClientSecret, _config.Site);
            await client.AuthenticateAsync();
            var (zones, error) = await client.GetZonesAsync();

            if (error != null)
            {
                StatusText.Text = $"Couldn't fetch zone list: {error}";
                return;
            }

            string list = string.Join("\n", zones.Select(z => $"{z.Id}  -  {z.Name}"));
            MessageBox.Show(this, list, "Raid tiers (id - name) - type the id you want into Zone ID",
                MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = $"Found {zones.Count} zone(s). Pick the id for the tier you want.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error fetching zones: {ex.Message}";
        }
        finally
        {
            ListZonesButton.IsEnabled = true;
        }
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptionsFromUi(out var error);
        if (options == null)
        {
            StatusText.Text = error;
            return;
        }
        await RunPipelineAsync(options);
    }

    private async Task RunPipelineAsync(RunOptions options)
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
            });

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