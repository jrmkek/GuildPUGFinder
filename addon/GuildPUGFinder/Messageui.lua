-- MessageUI.lua
-- Displays GuildPUGFinderDB.eligible (written by the Windows app) in a
-- simple in-game panel, with a per-person "Whisper" button and a
-- "Message All" button. Uses SendChatMessage directly - a real whisper
-- per click, no paste/clipboard tricks.
--
-- Also maintains GuildPUGFinderDB.messagedBlacklist - a PERSISTENT (saved
-- to disk, survives /reload and relogging) list of names not to message
-- again. Sending a whisper to someone auto-blacklists them; a checkbox
-- per row lets you also blacklist (or un-blacklist) manually, e.g. to
-- skip someone without messaging them, or to deliberately re-contact
-- someone later.

local ADDON_NAME = ...

local frame -- created lazily on first open
local rowFrames = {} -- reused row widgets, indexed 1..N
local RefreshList, MessageAll -- forward declarations, defined below

local DEFAULT_MESSAGE = "Hey! Want to join our raid?"

--------------------------------------------------------------------------
-- Persistent messaging blacklist (separate table from the addon's own
-- "pending"/"eligible" - never touched by ScanLFGList or overwritten by
-- the Windows app, so it survives every future /pugscan and reload).
--------------------------------------------------------------------------
GuildPUGFinderDB = GuildPUGFinderDB or {}
GuildPUGFinderDB.messagedBlacklist = GuildPUGFinderDB.messagedBlacklist or {}

local function EnsureBlacklistTable()
    GuildPUGFinderDB = GuildPUGFinderDB or {}
    GuildPUGFinderDB.messagedBlacklist = GuildPUGFinderDB.messagedBlacklist or {}
end

local function IsBlacklisted(name)
    EnsureBlacklistTable()
    return GuildPUGFinderDB.messagedBlacklist[name] == true
end

local function SetBlacklisted(name, blacklisted)
    EnsureBlacklistTable()
    if blacklisted then
        GuildPUGFinderDB.messagedBlacklist[name] = true
    else
        GuildPUGFinderDB.messagedBlacklist[name] = nil
    end
end

--------------------------------------------------------------------------
-- Row widget: blacklist checkbox + name label + a Whisper button
--------------------------------------------------------------------------
local ROW_HEIGHT = 22

local function CreateRow(parent, index)
    local row = CreateFrame("Frame", nil, parent)
    row:SetSize(300, ROW_HEIGHT)
    row:SetPoint("TOPLEFT", 0, -(index - 1) * ROW_HEIGHT)

    row.blacklistCheck = CreateFrame("CheckButton", nil, row, "UICheckButtonTemplate")
    row.blacklistCheck:SetSize(20, 20)
    row.blacklistCheck:SetPoint("LEFT", 0, 0)

    row.nameText = row:CreateFontString(nil, "OVERLAY", "GameFontNormal")
    row.nameText:SetPoint("LEFT", row.blacklistCheck, "RIGHT", 4, 0)
    row.nameText:SetWidth(180)
    row.nameText:SetJustifyH("LEFT")

    row.whisperButton = CreateFrame("Button", nil, row, "UIPanelButtonTemplate")
    row.whisperButton:SetSize(70, 20)
    row.whisperButton:SetPoint("RIGHT", -4, 0)
    row.whisperButton:SetText("Whisper")

    return row
end

--------------------------------------------------------------------------
-- Main frame
--------------------------------------------------------------------------
local function EnsureFrame()
    if frame then return frame end

    frame = CreateFrame("Frame", "GuildPUGFinderMessageFrame", UIParent, BackdropTemplateMixin and "BackdropTemplate" or nil)
    frame:SetSize(360, 420)
    frame:SetPoint("CENTER")
    frame:SetMovable(true)
    frame:EnableMouse(true)
    frame:RegisterForDrag("LeftButton")
    frame:SetScript("OnDragStart", frame.StartMoving)
    frame:SetScript("OnDragStop", frame.StopMovingOrSizing)
    frame:SetClampedToScreen(true)
    frame:Hide()

    if frame.SetBackdrop then
        pcall(function()
            frame:SetBackdrop({
                bgFile = "Interface/DialogFrame/UI-DialogBox-Background",
                edgeFile = "Interface/DialogFrame/UI-DialogBox-Border",
                tile = true, tileSize = 32, edgeSize = 32,
                insets = { left = 11, right = 12, top = 12, bottom = 11 },
            })
        end)
    end

    local title = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightLarge")
    title:SetPoint("TOP", 0, -16)
    title:SetText("GuildPUGFinder - Eligible")

    local closeButton = CreateFrame("Button", nil, frame, "UIPanelCloseButton")
    closeButton:SetPoint("TOPRIGHT", -4, -4)

    local msgLabel = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
    msgLabel:SetPoint("TOPLEFT", 20, -44)
    msgLabel:SetText("Message:")

    frame.messageBox = CreateFrame("EditBox", nil, frame, "InputBoxTemplate")
    frame.messageBox:SetSize(280, 20)
    frame.messageBox:SetPoint("TOPLEFT", 30, -60)
    frame.messageBox:SetAutoFocus(false)
    frame.messageBox:SetText(DEFAULT_MESSAGE)

    frame.messageAllButton = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    frame.messageAllButton:SetSize(120, 22)
    frame.messageAllButton:SetPoint("TOPLEFT", 20, -90)
    frame.messageAllButton:SetText("Message All")

    frame.refreshButton = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    frame.refreshButton:SetSize(120, 22)
    frame.refreshButton:SetPoint("LEFT", frame.messageAllButton, "RIGHT", 10, 0)
    frame.refreshButton:SetText("Refresh")

    local hint = frame:CreateFontString(nil, "OVERLAY", "GameFontDisableSmall")
    hint:SetPoint("TOPLEFT", 20, -114)
    hint:SetText("Checkbox = don't message (saved permanently)")

    frame.scrollFrame = CreateFrame("ScrollFrame", nil, frame, "UIPanelScrollFrameTemplate")
    frame.scrollFrame:SetPoint("TOPLEFT", 20, -132)
    frame.scrollFrame:SetPoint("BOTTOMRIGHT", -36, 20)

    frame.scrollChild = CreateFrame("Frame", nil, frame.scrollFrame)
    frame.scrollChild:SetSize(300, 1)
    frame.scrollFrame:SetScrollChild(frame.scrollChild)

    frame.refreshButton:SetScript("OnClick", function() RefreshList() end)
    frame.messageAllButton:SetScript("OnClick", function() MessageAll() end)

    return frame
end

--------------------------------------------------------------------------
-- Populate / refresh the candidate list from GuildPUGFinderDB.eligible
--------------------------------------------------------------------------
RefreshList = function()
    EnsureFrame()

    local eligible = GuildPUGFinderDB and GuildPUGFinderDB.eligible or {}
    local names = {}
    for name in pairs(eligible) do
        table.insert(names, name)
    end
    table.sort(names)

    for i, name in ipairs(names) do
        local row = rowFrames[i]
        if not row then
            row = CreateRow(frame.scrollChild, i)
            rowFrames[i] = row
        end
        row:Show()
        row:ClearAllPoints()
        row:SetPoint("TOPLEFT", 0, -(i - 1) * ROW_HEIGHT)

        local data = eligible[name] or {}
        local specText = data.spec and (" - " .. data.spec) or ""
        local classText = data.className and (" [" .. data.className .. "]") or ""
        row.nameText:SetText(name .. classText .. specText)

        local blacklisted = IsBlacklisted(name)
        row.blacklistCheck:SetChecked(blacklisted)
        row.whisperButton:SetEnabled(not blacklisted)

        row.blacklistCheck:SetScript("OnClick", function(self)
            SetBlacklisted(name, self:GetChecked())
            row.whisperButton:SetEnabled(not self:GetChecked())
        end)

        row.whisperButton:SetScript("OnClick", function()
            local msg = frame.messageBox:GetText()
            if msg and msg ~= "" then
                SendChatMessage(msg, "WHISPER", nil, name)
                print(("|cff00ff00GuildPUGFinder:|r whispered %s"):format(name))
                SetBlacklisted(name, true)
                row.blacklistCheck:SetChecked(true)
                row.whisperButton:SetEnabled(false)
            end
        end)
    end

    for i = #names + 1, #rowFrames do
        rowFrames[i]:Hide()
    end

    frame.scrollChild:SetHeight(math.max(#names * ROW_HEIGHT, 1))

    print(("|cff00ff00GuildPUGFinder:|r loaded %d eligible candidate(s)."):format(#names))
end

--------------------------------------------------------------------------
-- Message All: whispers everyone currently listed and NOT already
-- blacklisted, staggered to avoid WoW's chat spam throttle. Each one gets
-- auto-blacklisted right after sending, same as the individual button.
--------------------------------------------------------------------------
MessageAll = function()
    EnsureFrame()
    local eligible = GuildPUGFinderDB and GuildPUGFinderDB.eligible or {}
    local msg = frame.messageBox:GetText()
    if not msg or msg == "" then
        print("|cffff0000GuildPUGFinder:|r message box is empty.")
        return
    end

    local names = {}
    for name in pairs(eligible) do
        if not IsBlacklisted(name) then
            table.insert(names, name)
        end
    end
    table.sort(names)

    if #names == 0 then
        print("|cffffcc00GuildPUGFinder:|r nothing to send - everyone listed is already blacklisted/messaged.")
        return
    end

    for i, name in ipairs(names) do
        C_Timer.After((i - 1) * 0.3, function()
            SendChatMessage(msg, "WHISPER", nil, name)
            SetBlacklisted(name, true)
            print(("|cff00ff00GuildPUGFinder:|r whispered %s (%d/%d)"):format(name, i, #names))
            if i == #names then
                RefreshList() -- one refresh at the end to update checkboxes/buttons
            end
        end)
    end
end

--------------------------------------------------------------------------
-- Slash command: /pugmsg opens (and refreshes) the panel
--------------------------------------------------------------------------
SLASH_GUILDPUGFINDERMSG1 = "/pugmsg"
SlashCmdList["GUILDPUGFINDERMSG"] = function()
    EnsureFrame()
    RefreshList()
    frame:Show()
end