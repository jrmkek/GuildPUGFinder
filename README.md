# GuildPUGFinder

Screen pickup-group candidates in World of Warcraft against their WarcraftLogs
history before you invite them.

The in-game addon collects names — Group Finder listing leaders and people
spamming LFM/LFG in chat — into SavedVariables. A companion Windows app reads
that file, looks each name up on WarcraftLogs, and tells you who clears your
parse and item-level bar. Passing names are written back into the same file so
the addon can use them in a future version.

> **Not affiliated with Blizzard Entertainment or WarcraftLogs.** You need your
> own WarcraftLogs API client to use this.

---

## How it works

```
  In-game                    On disk                      Companion app
 ─────────────────────────────────────────────────────────────────────────────
  Group Finder listings  ─┐
                          ├─▶  GuildPUGFinderDB.pending  ─▶  read + parse
  LFM/LFG chat spam      ─┘         (SavedVariables)              │
                                                                  ▼
                                                       WarcraftLogs v2 GraphQL
                                                        (parse %, item level)
                                                                  │
                                                                  ▼
                          GuildPUGFinderDB.eligible  ◀──  filter + write back
```

The addon only writes to disk when the game saves SavedVariables — on
`/reload`, logout, or exit. That is why the workflow below ends in a
`/reload`, and why the companion's watch mode keys off that file changing.

---

## Requirements

| | |
|---|---|
| Game client | Interface 20504 (Burning Crusade-era / Anniversary realms) |
| Companion app | Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build |
| API access | A WarcraftLogs V2 API client (free) |

The addon's Group Finder scan relies on the flat-return form of
`C_LFGList.GetSearchResultInfo`, where `leader` is the 12th return value. That
is the classic/backport signature — it is **not** compatible with modern
retail's table-based API.

---

## Setup

### 1. Install the addon

Copy the `addon/GuildPUGFinder` folder into your AddOns directory, so you end
up with:

```
World of Warcraft/_anniversary_/Interface/AddOns/GuildPUGFinder/
    Core.lua
    GuildPUGFinder.toc
```

Enable it at the character-select screen and log in. You should see a green
`GuildPUGFinder loaded.` line in chat.

### 2. Get WarcraftLogs API credentials

Go to your WarcraftLogs profile → **API Clients** → **Create Client** -> **Manage your V2 Clients**. Any name
and redirect URL will do; this app uses the client-credentials flow and never
opens a browser. Copy the client ID and secret.

### 3. Configure the companion app

```powershell
cd app/GuildPUGFinderApp
Copy-Item config.example.json config.json
```

Then edit `config.json`:

| Key | What it is |
|---|---|
| `ClientId` / `ClientSecret` | From step 2. |
| `Site` | `fresh`, `classic`, or `retail` — picks which WarcraftLogs domain to query. |
| `ServerSlug` | Your realm, lowercase and hyphenated, e.g. `spineshatter`. |
| `ServerRegion` | `eu`, `us`, etc. |
| `SavedVariablesPath` | Full path to `WTF/Account/<AccountName>/SavedVariables/GuildPUGFinder.lua`. |

`config.json` is gitignored. **Never commit it** — the client secret is a
credential, and anyone holding it can spend your API rate limit.

### 4. Build and run

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1            # publishes to dist/
powershell -ExecutionPolicy Bypass -File .\build.ps1       # publish, then launch
```

Or straight from the SDK:

```powershell
dotnet run --project app/GuildPUGFinderApp
```

---

## Using it

1. **In game**, open the Group Finder and search for the activity you care
   about, so listings are actually loaded client-side.
2. Type `/pugscan`. It reports how many listings it saw and how many names it
   captured. Chat-based LFM/LFG names accumulate passively in the background
   the whole time you are logged in.
3. Type `/reload`. This is what flushes SavedVariables to disk.
4. **In the app**, set your thresholds and hit **Run**. Results stream into the
   grid as each lookup returns.

Or press **Start Watching** once and skip step 4 — the app re-runs itself
whenever the file changes, so `/pugscan` + `/reload` is the whole loop.

### Slash commands

| Command | Effect |
|---|---|
| `/pugscan` | Scan currently loaded Group Finder results. |
| `/pugscan list` | Print pending candidates to chat. |
| `/pugscan dump` | Print raw `GetSearchResultInfo` fields — use this if the scan looks wrong on your client build. |
| `/pugscan clear` | Empty the pending list. |

### Reading the results grid

| Status | Meaning |
|---|---|
| `Pass` | Met every active filter. Written to `GuildPUGFinderDB.eligible`. |
| `Fail` | Has logs, but below one of your thresholds. |
| `NoData` | Character exists on WarcraftLogs but has no rankings for this zone. |
| `NotFound` | No such character on that realm and region. |
| `Error` | The lookup itself failed — see the **Error** column for the raw text. |

`Error` is deliberately kept separate from `NoData`. A rate-limited or
malformed query looks identical to "this player has never raided" unless you
distinguish them, and silently treating failures as no-data is how you end up
rejecting good players.

### Filters

- **Min best parse %** — against `bestPerformanceAverage` for the zone.
- **Min average item level** — averaged across the item levels recorded on the
  character's best ranked kills.
- **Also require EVERY boss parse ≥** — additionally gates on each individual
  boss. Catches the "carried on one fight" case that a zone average hides.
- **Partition** — WarcraftLogs' content-phase segmentation. Blank means the
  all-time aggregate. The number-to-phase mapping is site-specific and is
  worth confirming against the website before trusting it.
- **Classes** — unchecked means any class. When the addon captured a
  candidate's class in-game, a mismatch skips the API call entirely, which
  saves rate limit.

### Rate limiting

WarcraftLogs bills queries against an hourly point budget. Before a run, the
app checks the remaining budget and **aborts the whole run** if it looks too
thin for the number of candidates queued, rather than burning what is left and
returning a screenful of misleading `NoData`. If you hit this, the message
tells you how long until the reset.

---

## Repository layout

```
addon/GuildPUGFinder/     The in-game addon (Lua). Copy this folder to AddOns/.
    Core.lua              Scanning, chat capture, slash commands.
    GuildPUGFinder.toc    Addon manifest.

app/
    GuildPUGFinder.sln
    GuildPUGFinderApp/    WPF companion (.NET 8).
        MainWindow.xaml       UI layout.
        MainWindow.xaml.cs    UI wiring, file watcher, debouncing.
        PipelineRunner.cs     Orchestrates read → query → filter → write back.
        WarcraftLogsClient.cs OAuth + GraphQL against the WCL v2 API.
        LuaTableParser.cs     Minimal reader/writer for SavedVariables syntax.
        Config.cs             One-time setup (credentials, realm, path).
        RunOptions.cs         Per-run filters, plus result types.
        config.example.json   Configuration template.

samples/                  A captured WarcraftLogs response, kept as a
                          reference for the shape the parser expects.
docs/                     Additional notes.
```

`LuaTableParser` is not a general Lua parser. It handles exactly the subset of
table syntax that WoW's SavedVariables serializer emits, which is enough to
round-trip this addon's own output and nothing more.

---

## Troubleshooting

**"No Group Finder results currently loaded."** The addon reads what the client
has already fetched. Open the Group Finder and run a search first.

**"C_LFGList API not available on this client."** Your client build does not
expose that API. The addon targets Interface 20504.

**Every candidate comes back `NotFound`.** Check `ServerSlug` and
`ServerRegion`. The slug is lowercase and hyphenated, and it has to match the
realm the characters actually raid on.

**The app says `config.json not found`.** It looks next to the executable, then
in the working directory. If you are running from `dist/`, the file needs to be
there too — `build.ps1` copies it for you.

**Nothing happens when watching.** SavedVariables is only written on `/reload`,
logout, or exit. Scanning alone does not touch the disk.

---

## Known gaps

- `GuildPUGFinderDB.eligible` is written by the app but nothing in the addon
  reads it back yet, so eligible players are not surfaced in-game.
- The chat keyword list in `Core.lua` is tuned for Burning Crusade raid names
  and will need editing for other content.
- Realm is taken from `config.json` for every lookup, so cross-realm
  candidates from the Group Finder resolve against the wrong realm.
- There are no automated tests. `LuaTableParser` is the obvious first target.

## License

[MIT](LICENSE).
