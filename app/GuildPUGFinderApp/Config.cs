// Config.cs
// Stable, one-time-setup settings live here (loaded from config.json).
// Per-run tunable filters (thresholds, classes, per-boss toggle) live in
// RunOptions instead, since those are meant to be changed often from the UI.

namespace GuildPUGFinderApp;

public class Config
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string Site { get; init; } = "fresh";
    public string ServerSlug { get; init; } = "";
    public string ServerRegion { get; init; } = "";
    public string SavedVariablesPath { get; init; } = "";
}