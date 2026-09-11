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
}

public enum CandidateStatus { Pass, Fail, NoData, NotFound, Error }

public record CandidateRow(
    string Name,
    CandidateStatus Status,
    double? BestParsePercent,
    double? AverageItemLevel,
    string? Spec,
    string? ClassName,
    List<BossParse> PerBoss,
    string? ErrorMessage = null
);
