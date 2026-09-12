-- GuildPUGFinder - Core.lua
-- Step 1: collect candidate names from the Group Finder (LFGList) tool and
-- write them to SavedVariables so an external app can read them after a
-- /reload or logout.

local ADDON_NAME = ...

--------------------------------------------------------------------------
-- SavedVariables setup
--------------------------------------------------------------------------
-- Structure written to disk:
-- GuildPUGFinderDB = {
--   pending = {
--     ["LeaderName"] = {
--       source    = "lfglist",
--       activity  = "<listing title>",
--       comment   = "<listing description>",
--       ilvl      = <number>,
--       members   = <number>,
--       seenAt    = <epoch seconds>,
--     },
--     ...
--   },
--   lastScan = <epoch seconds>,
-- }
GuildPUGFinderDB = GuildPUGFinderDB or { pending = {}, lastScan = 0 }

local function AddCandidate(name, data)
    if not name or name == "" then return end
    GuildPUGFinderDB.pending[name] = GuildPUGFinderDB.pending[name] or {}
    for k, v in pairs(data) do
        GuildPUGFinderDB.pending[name][k] = v
    end
    GuildPUGFinderDB.pending[name].seenAt = time()
end

--------------------------------------------------------------------------
-- Group Finder (LFGList) scanning
--------------------------------------------------------------------------
-- Confirmed for this client build via a live /pugscan dump:
--   numResults, resultIDs = C_LFGList.GetSearchResults()
--   info = C_LFGList.GetSearchResultInfo(resultID)  -- a table with fields:
--     info.leaderName, info.name, info.comment,
--     info.requiredItemLevel, info.numMembers, ...

local function ScanLFGList()
    if not C_LFGList or not C_LFGList.GetSearchResults then
        print("|cffff0000GuildPUGFinder:|r C_LFGList API not available on this client.")
        return
    end

    local numResults, resultIDs = C_LFGList.GetSearchResults()
    if not numResults or numResults == 0 or not resultIDs then
        print("|cffffcc00GuildPUGFinder:|r No Group Finder results currently loaded. Open the Group Finder panel and search first.")
        return
    end

    -- Fresh snapshot every scan: wipe stale candidates from a previous
    -- activity/category before capturing the current search's results.
    GuildPUGFinderDB.pending = {}

    local found = 0
    for _, resultID in ipairs(resultIDs) do
        local info = C_LFGList.GetSearchResultInfo(resultID)
        if type(info) == "table" then
            local leader = info.leaderName
            local numMembers = info.numMembers or 1

            -- Skip listings where the leader already has a group (they're
            -- recruiting FOR their own raid, not looking to join ours).
            -- Only solo self-listings (numMembers == 1) are candidates.
            if leader and leader ~= "" and numMembers <= 1 then
                -- Confirmed via live /pugscan dumpmember: member(1) returns
                -- role, classFileName, className, ..., isLeader, ...
                -- className (3rd return) is the properly-capitalized name
                -- ("Hunter", "Warrior", etc) that matches the Windows app's
                -- class filter list.
                local className = nil
                local role = nil
                if C_LFGList.GetSearchResultMemberInfo then
                    local ok, r, _, cName = pcall(C_LFGList.GetSearchResultMemberInfo, resultID, 1)
                    if ok then
                        className = cName
                        role = r
                    end
                end

                AddCandidate(leader, {
                    source    = "lfglist",
                    activity  = info.name,
                    comment   = info.comment,
                    ilvl      = info.requiredItemLevel,
                    members   = info.numMembers,
                    className = className,
                    role      = role,
                })
                found = found + 1
            end
        end
    end

    GuildPUGFinderDB.lastScan = time()
    print(("|cff00ff00GuildPUGFinder:|r scanned %d listing(s), captured %d name(s)."):format(numResults, found))
end

-- Debug helper: dump the raw info table for every visible result so you can
-- see the actual field names your client build provides.
local function DumpLFGList()
    if not C_LFGList or not C_LFGList.GetSearchResults then return end
    local numResults, resultIDs = C_LFGList.GetSearchResults()
    print(("GuildPUGFinder: %s result(s)"):format(tostring(numResults)))
    for _, resultID in ipairs(resultIDs or {}) do
        local info = C_LFGList.GetSearchResultInfo(resultID)
        print("---- resultID", resultID, "----")
        if type(info) == "table" then
            for k, v in pairs(info) do
                print("  ", tostring(k), "=", tostring(v))
            end
        else
            print("  (not a table, got:", type(info), ")")
        end
    end
end

-- Debug helper: test whether GetSearchResultMemberInfo exposes class data
-- for a listing's member(s) - if it does, we could skip querying WCL for
-- candidates whose class we already know isn't wanted, saving rate-limit
-- budget. Unverified until tested live.
local function DumpMemberInfo()
    if not C_LFGList or not C_LFGList.GetSearchResults then return end
    local numResults, resultIDs = C_LFGList.GetSearchResults()
    print(("GuildPUGFinder: testing member info on %s result(s)"):format(tostring(numResults)))

    for _, resultID in ipairs(resultIDs or {}) do
        local info = C_LFGList.GetSearchResultInfo(resultID)
        local leaderName = type(info) == "table" and info.leaderName or "?"
        print(("---- resultID %s (leader: %s) ----"):format(tostring(resultID), leaderName))

        if C_LFGList.GetSearchResultMemberInfo then
            local ok, a, b, c, d, e, f, g, h = pcall(C_LFGList.GetSearchResultMemberInfo, resultID, 1)
            if ok then
                print("  member(1) raw returns:", tostring(a), tostring(b), tostring(c), tostring(d), tostring(e), tostring(f), tostring(g), tostring(h))
                -- if the first return is itself a table, dump its fields too
                if type(a) == "table" then
                    for k, v in pairs(a) do
                        print("    ", tostring(k), "=", tostring(v))
                    end
                end
            else
                print("  GetSearchResultMemberInfo call failed:", tostring(a))
            end
        else
            print("  C_LFGList.GetSearchResultMemberInfo does not exist on this client.")
        end
    end
end

-- Debug helper: test whether GetSearchResultMemberCounts is the real
-- source of the role icons shown in Blizzard's own Group Finder panel -
-- our GetSearchResultMemberInfo-based role capture has been shown wrong
-- (a confirmed healer was captured as DAMAGER), so this checks the other
-- likely candidate function instead.
local function DumpMemberCounts()
    if not C_LFGList or not C_LFGList.GetSearchResults then return end
    local numResults, resultIDs = C_LFGList.GetSearchResults()
    print(("GuildPUGFinder: testing member counts on %s result(s)"):format(tostring(numResults)))

    for _, resultID in ipairs(resultIDs or {}) do
        local info = C_LFGList.GetSearchResultInfo(resultID)
        local leaderName = type(info) == "table" and info.leaderName or "?"

        if C_LFGList.GetSearchResultMemberCounts then
            local ok, a, b, c, d, e, f, g = pcall(C_LFGList.GetSearchResultMemberCounts, resultID)
            if ok then
                print(("%s: raw returns: %s %s %s %s %s %s %s"):format(
                    leaderName, tostring(a), tostring(b), tostring(c), tostring(d), tostring(e), tostring(f), tostring(g)))
            else
                print(("%s: GetSearchResultMemberCounts failed: %s"):format(leaderName, tostring(a)))
            end
        else
            print("C_LFGList.GetSearchResultMemberCounts does not exist on this client.")
            break
        end
    end
end

--------------------------------------------------------------------------
-- Event wiring
--------------------------------------------------------------------------
local frame = CreateFrame("Frame")
frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("LFG_LIST_SEARCH_RESULTS_RECEIVED")

frame:SetScript("OnEvent", function(self, event, ...)
    if event == "ADDON_LOADED" then
        local loaded = ...
        if loaded == ADDON_NAME then
            print("|cff00ff00GuildPUGFinder|r loaded. Use /pugscan to scan Group Finder, /pugscan dump to inspect raw fields.")
        end
        return
    end

    if event == "LFG_LIST_SEARCH_RESULTS_RECEIVED" then
        -- Fires when a new search (new category/activity/refresh) returns
        -- results. Auto re-scan so the list stays current without a manual
        -- /pugscan every time you switch raids.
        ScanLFGList()
    end
end)

--------------------------------------------------------------------------
-- Slash command
--------------------------------------------------------------------------
SLASH_GUILDPUGFINDER1 = "/pugscan"
SlashCmdList["GUILDPUGFINDER"] = function(msg)
    msg = (msg or ""):trim():lower()
    if msg == "dump" then
        DumpLFGList()
    elseif msg == "dumpmember" then
        DumpMemberInfo()
    elseif msg == "dumpcounts" then
        DumpMemberCounts()
    elseif msg == "clear" then
        GuildPUGFinderDB.pending = {}
        print("|cff00ff00GuildPUGFinder:|r cleared pending candidate list.")
    elseif msg == "list" then
        local count = 0
        for name, data in pairs(GuildPUGFinderDB.pending) do
            count = count + 1
            print(("  %s  [%s]  %s"):format(
                name, data.source, data.activity or ""))
        end
        print(("|cff00ff00GuildPUGFinder:|r %d pending candidate(s)."):format(count))
    else
        ScanLFGList()
    end
end