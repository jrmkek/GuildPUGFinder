// RunOptions.cs
// Tunable filters for one run of the pipeline - these are the values the
// WPF window lets the user change before hitting "Run".

namespace GuildPUGFinderApp;

public class RunOptions
{
    public double MinBestParsePercent { get; set; } = 50;
    public double MinAverageItemLevel { get; set; } = 125;

    // Empty = allow any class.
    public HashSet<string> AllowedClasses { get; set; } = new();

    // When true, ALSO require every logged boss's rankPercent to be >=
    // MinPerBossParsePercent, not just the overall zone average. Catches
    // "carried on one boss" cases the overall average can hide.
    public bool UsePerBossParse { get; set; } = false;
    public double MinPerBossParsePercent { get; set; } = 40;

    // null = all-time aggregate (default). Set to filter to a specific WCL
    // content phase/partition - the exact number-to-phase mapping needs
    // confirming live (see UI note).
    public int? Partition { get; set; } = null;

    // null = WCL's default (currently latest/BT+Hyjal). Set to target a
    // specific raid tier (Karazhan, SSC/TK, etc) - use "List Raid Tiers" in
    // the UI to find the right id.
    public int? ZoneId { get; set; } = null;

    // Which parse categories to actually query. Unchecking one skips that
    // query entirely (saves rate-limit budget) rather than just hiding it
    // from the display.
    public bool QueryOverall { get; set; } = true;
    public bool QueryDps { get; set; } = true;
    public bool QueryHeal { get; set; } = true;
    public bool QueryTank { get; set; } = true;

    // Names to skip entirely - no query, no row, regardless of what a
    // fresh /pugscan + /reload brings in. Persists for the life of the
    // app (set from the UI's right-click blacklist action).
    public HashSet<string> BlacklistedNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum CandidateStatus { Pass, Fail, NoData, NotFound, Error }

public record CandidateRow(
    string Name,
    CandidateStatus Status,
    double? OverallParsePercent,
    double? DpsParsePercent,
    double? HealParsePercent,
    double? TankParsePercent,
    double? AverageItemLevel,
    string? Spec,
    string? ClassName,
    List<BossParse> PerBoss,
    string? ErrorMessage = null
);