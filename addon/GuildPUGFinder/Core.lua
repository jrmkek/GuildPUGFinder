-- GuildPUGFinder - Core.lua
-- Step 1: collect candidate names from the Group Finder (LFGList) tool and
-- from LFM/LFG chat spam, and write them to SavedVariables so an external
-- app can read them after a /reload or logout.

local ADDON_NAME = ...

--------------------------------------------------------------------------
-- SavedVariables setup
--------------------------------------------------------------------------
-- Structure written to disk:
-- GuildPUGFinderDB = {
--   pending = {
--     ["Playername-Realm"] = {
--       source    = "lfglist" | "chat",
--       activity  = "<listing title, lfglist only>",
--       comment   = "<listing description, lfglist only>",
--       ilvl      = <number, lfglist only>,
--       members   = <number, lfglist only>,
--       channel   = "<chat event name, chat only>",
--       message   = "<raw chat line, chat only>",
--       seenAt    = <epoch seconds>,
--     },
--     ...
--   },
--   lastScan = <epoch seconds>,
-- }
GuildPUGFinderDB = GuildPUGFinderDB or { pending = {}, lastScan = 0 }

local function AddCandidate(name, data)
    if not name or name == "" then return end
    -- normalize: strip realm if present for now, keep both forms available
    GuildPUGFinderDB.pending[name] = GuildPUGFinderDB.pending[name] or {}
    for k, v in pairs(data) do
        GuildPUGFinderDB.pending[name][k] = v
    end
    GuildPUGFinderDB.pending[name].seenAt = time()
end

--------------------------------------------------------------------------
-- Group Finder (LFGList) scanning
--------------------------------------------------------------------------
-- Confirmed for this client build:
--   numResults, resultIDs = C_LFGList.GetSearchResults()
--   id, _, name, desc, _, ilvl, timeElapsed, _, _, _, _, leader, members, _
--       = C_LFGList.GetSearchResultInfo(resultID)
-- i.e. GetSearchResultInfo returns flat values, not a struct table, and
-- "leader" is the 12th return value. This is the classic/backport-era
-- signature, distinct from modern retail's table-based API.

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

    local found = 0
    for _, resultID in ipairs(resultIDs) do
        local id, _, name, desc, _, ilvl, timeElapsed, _, _, _, _, leader, members =
            C_LFGList.GetSearchResultInfo(resultID)

        if leader and leader ~= "" then
            AddCandidate(leader, {
                source   = "lfglist",
                activity = name,
                comment  = desc,
                ilvl     = ilvl,
                members  = members,
            })
            found = found + 1
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
        local id, unk1, name, desc, unk2, ilvl, timeElapsed, unk3, unk4, unk5, unk6, leader, members, unk7 =
            C_LFGList.GetSearchResultInfo(resultID)
        print("---- resultID", resultID, "----")
        print("  id       =", tostring(id))
        print("  name     =", tostring(name))
        print("  desc     =", tostring(desc))
        print("  ilvl     =", tostring(ilvl))
        print("  leader   =", tostring(leader))
        print("  members  =", tostring(members))
    end
end

--------------------------------------------------------------------------
-- Chat scanning (LFG/LFM spam in Trade / LFG channels, yell, say)
--------------------------------------------------------------------------
-- Very deliberately simple keyword match. Tune this list to your server's
-- conventions (raid abbreviations, "need heals", etc.) once you see real
-- chat traffic in /pugscan dumpchat or via the saved table.
local KEYWORDS = {
    "LFM", "LFG", "LOOKING FOR", "NEED %d+", "RAID INV", "KARA", "GRUUL",
    "MAGTHERIDON", "SSC", "TK", "HYJAL", "BT", "SUNWELL",
}

local function MessageLooksLikeGroupCall(msg)
    local upper = msg:upper()
    for _, kw in ipairs(KEYWORDS) do
        if upper:find(kw) then
            return true
        end
    end
    return false
end

local function OnChatMessage(event, message, sender, ...)
    if not MessageLooksLikeGroupCall(message) then return end
    -- sender comes through as "Name" or "Name-Realm" depending on channel
    local name = sender:match("^([^%-]+)") or sender
    AddCandidate(name, {
        source  = "chat",
        channel = event,
        message = message,
    })
end

--------------------------------------------------------------------------
-- Event wiring
--------------------------------------------------------------------------
local frame = CreateFrame("Frame")
frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("CHAT_MSG_CHANNEL")
frame:RegisterEvent("CHAT_MSG_YELL")
frame:RegisterEvent("CHAT_MSG_SAY")

frame:SetScript("OnEvent", function(self, event, ...)
    if event == "ADDON_LOADED" then
        local loaded = ...
        if loaded == ADDON_NAME then
            print("|cff00ff00GuildPUGFinder|r loaded. Use /pugscan to scan Group Finder, /pugscan dump to inspect raw fields.")
        end
        return
    end

    if event == "CHAT_MSG_CHANNEL" or event == "CHAT_MSG_YELL" or event == "CHAT_MSG_SAY" then
        local message, sender = ...
        OnChatMessage(event, message, sender)
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
    elseif msg == "clear" then
        GuildPUGFinderDB.pending = {}
        print("|cff00ff00GuildPUGFinder:|r cleared pending candidate list.")
    elseif msg == "list" then
        local count = 0
        for name, data in pairs(GuildPUGFinderDB.pending) do
            count = count + 1
            print(("  %s  [%s]  %s"):format(name, data.source, data.activity or data.message or ""))
        end
        print(("|cff00ff00GuildPUGFinder:|r %d pending candidate(s)."):format(count))
    else
        ScanLFGList()
    end
end
