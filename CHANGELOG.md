# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Repository structure split into `addon/` (the in-game WoW addon) and
  `app/` (the WPF companion), with a solution file and a `build.ps1` that
  publishes a self-contained Windows binary.
- `config.example.json` as the committed configuration template.
- README, LICENSE (MIT), this changelog, and `.editorconfig`.

### Changed

- All C# types now live in the `GuildPUGFinderApp` namespace; previously only
  the XAML code-behind was namespaced and the rest sat in the global one.
- `config.json` is gitignored and no longer carries real credentials.

### Removed

- Checked-in `bin/` and `obj/` build output, a stale duplicate
  `MainWindow.xaml`, and a `files.zip` snapshot of the source tree.

## [0.1.0]

### Added

- WoW addon that captures Group Finder listing leaders and LFM/LFG chat
  senders into `GuildPUGFinderDB.pending`, with `/pugscan`, `/pugscan dump`,
  `/pugscan list`, and `/pugscan clear`.
- WPF companion that reads the SavedVariables file, queries the WarcraftLogs
  v2 GraphQL API for each candidate's best parse percentage and average item
  level, filters on configurable thresholds and class, and writes passing
  candidates back to `GuildPUGFinderDB.eligible`.
- Optional per-boss parse gate, a live-updating results grid, and a
  filesystem watcher that re-runs the pipeline when the game saves.
- Pre-flight WarcraftLogs rate-limit check that aborts a run rather than
  returning a wall of misleading "no data" results.
