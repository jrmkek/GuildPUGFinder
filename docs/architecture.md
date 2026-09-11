# Architecture notes

Background on why the pieces are shaped the way they are. For setup and usage,
see the [README](../README.md).

## Why two programs

The addon cannot make HTTP requests. WoW's Lua sandbox has no network access at
all, by design, so a character's WarcraftLogs history is simply unreachable
from inside the game. SavedVariables is the only channel out, which makes the
split unavoidable: the addon collects names, an external process does the
lookups, and the file is the interface between them.

That interface is one-directional in practice. The game writes SavedVariables
only when it decides to — on `/reload`, logout, or exit — and it overwrites the
whole file from its in-memory table when it does. Anything the companion writes
into that file is therefore only visible to the addon on the *next* load, and
is destroyed if the game saves before the addon has read it. The current
`eligible` write-back is safe only because the app writes it while the file is
idle between sessions.

## The data contract

```lua
GuildPUGFinderDB = {
    pending = {
        ["Playername"] = {
            source   = "lfglist" | "chat",
            activity = "<listing title>",     -- lfglist only
            comment  = "<listing description>",
            ilvl     = <number>,
            members  = <number>,
            className = "<class>",            -- when the client provided it
            channel  = "<chat event>",        -- chat only
            message  = "<raw chat line>",
            seenAt   = <epoch seconds>,
        },
    },
    eligible = {                              -- written by the companion app
        ["Playername"] = {
            bestParsePercent = <number>,
            medianParsePercent = <number>,
            averageItemLevel = <number>,
            spec = "<spec>",
            className = "<class>",
        },
    },
    lastScan = <epoch seconds>,
}
```

## Client API notes

`C_LFGList.GetSearchResultInfo` returns flat values on this client build, not a
struct table, and `leader` is the 12th return value:

```lua
local id, _, name, desc, _, ilvl, timeElapsed, _, _, _, _, leader, members =
    C_LFGList.GetSearchResultInfo(resultID)
```

Modern retail returns a single table instead. Porting the addon across client
generations means rewriting that call, not adjusting indices. `/pugscan dump`
exists precisely to check this against an unfamiliar build before trusting the
scan output.

## Class filtering happens twice

Once client-side and once server-side, for different reasons.

When the addon managed to capture a candidate's class in-game, the companion
compares it to the class filter *before* querying WarcraftLogs and skips the
call entirely on a mismatch. That is purely a rate-limit optimisation.

The second check, after the response comes back, prefers that addon-captured
class over the one WarcraftLogs resolves from `classID`. The WCL-derived name
is `null` whenever the query fails for any reason — rate limiting included —
so filtering on it alone silently drops candidates whose class was already
known. Preferring the locally captured value keeps a failed lookup from being
indistinguishable from a wrong-class candidate.

## Failure modes are kept distinct

`CandidateStatus` separates `NoData` (the character exists but has no rankings
in this zone) from `Error` (the query itself failed). Collapsing these is
tempting because both produce an empty result, but they mean opposite things:
one is a real signal about the player, the other is a signal about your API
connection. The raw diagnostic text is carried through on every row regardless
of which bucket it landed in.

The rate-limit pre-flight follows from the same reasoning. Exhausting the
hourly budget mid-run turns every remaining candidate into a false `NoData`, so
the run aborts up front with a message saying when the budget resets.

## File watching

Three complications, each handled in `MainWindow.xaml.cs`:

1. **Multiple events per save.** The OS, antivirus, and the writer itself can
   each raise events for one logical save, so a 1.5-second debounce timer waits
   for things to go quiet before reading.
2. **Other addons.** The watcher is directory-wide, because a write may land as
   a temp file plus a rename rather than an in-place edit. That means every
   other addon's SavedVariables write fires here too, so events are filtered by
   filename.
3. **Self-triggering.** A run writes back to the very file being watched, which
   would trigger another run. After each run the app records the file's content
   and skips any change whose content matches, breaking the loop.
