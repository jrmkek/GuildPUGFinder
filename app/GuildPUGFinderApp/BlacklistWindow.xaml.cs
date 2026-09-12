using System.Collections.ObjectModel;
using System.Windows;

namespace GuildPUGFinderApp;

public class BlacklistEntryView
{
    public string Name { get; }
    public string ExpiresIn { get; }
    public BlacklistEntryView(string name, DateTime blacklistedAt, TimeSpan ttl)
    {
        Name = name;
        var remaining = ttl - (DateTime.UtcNow - blacklistedAt);
        ExpiresIn = remaining.TotalMinutes < 1 ? "expiring now" : $"{remaining.Hours}h {remaining.Minutes}m";
    }
}

public partial class BlacklistWindow : Window
{
    // Same dictionary instance as MainWindow's _blacklist - removing here
    // directly affects every future run, no extra syncing needed.
    private readonly Dictionary<string, DateTime> _blacklist;
    private readonly ObservableCollection<BlacklistEntryView> _entries = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    public BlacklistWindow(Dictionary<string, DateTime> blacklist)
    {
        InitializeComponent();
        _blacklist = blacklist;
        EntriesGrid.ItemsSource = _entries;
        Refresh();
    }

    private void Refresh()
    {
        _entries.Clear();
        foreach (var (name, blacklistedAt) in _blacklist.OrderBy(kv => kv.Key))
        {
            _entries.Add(new BlacklistEntryView(name, blacklistedAt, Ttl));
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = EntriesGrid.SelectedItems.Cast<BlacklistEntryView>().Select(v => v.Name).ToList();
        foreach (var name in selected)
        {
            _blacklist.Remove(name);
        }
        Refresh();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
