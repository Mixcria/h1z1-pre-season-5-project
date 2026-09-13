--------------------------------------------------------------------------------------------------
-- CranberryMenu.lua - the client half of the Cranberry graphical mod menu (R6 recommendation b').
--
-- THIS IS AN ADDED FILE. It replaces nothing, patches nothing, and repacks nothing. Deleting it
-- restores the client exactly: the server's text menu (/m) keeps working, because the server only
-- switches to the Lua surface when it is told to (/surface lua).
--
-- Target: C:\Aug2017\Client\H1Z1.exe build 0.0.118.208059, Lua 5.1, UI\ScriptsBase.bin.
-- Written against the API that R6-graphical-menu.md proved exists; every call below is cited.
--
--------------------------------------------------------------------------------------------------
-- HOW THIS FILE GETS LOADED  (read this before anything else)
--
-- There is NO auto-load. Measured, not assumed:
--   * ClientConfig.ini [Paths] PathScripts=.\Resources\Scripts\ is only ever exposed to Lua as the
--     string Client.PathScripts. Nothing in H1Z1.exe scans that directory: the whole image contains
--     the literals "./Resources/Scripts/", "PathScripts" and "Paths" and nothing else script-shaped
--     - no "scripts.txt", no "*.lua" name, no "ScriptsBase" (checked ASCII and UTF-16 over all
--     72,818,304 bytes). The "Include new *.lua file in scripts.txt." line inside
--     dofileInternalOnlyCommand (proto[0,5,4]) is a build-time reminder from SOE's own tree; the
--     file it names does not exist in this build.
--   * Ui.ExecuteScript (1a 07) cannot name a file: every wire argument is stamped tag 1 (integer)
--     by FUN_140b7fdf0, so dofile / loadfile / require / loadstring are unreachable from the wire.
--   * The console-command -> Lua bridge is real (FUN_140bad090 compiles
--     "return function() if _G[[[<tail>]]] ~= nil then <name>(<tail>) else <name>([[<tail>]]) end end")
--     but its 64-bucket table is only ever filled by BindCommandToLua, and NO shipped script calls
--     it (the whole ScriptsBase.bin API surface has no BindCommandToLua site; the only non-self
--     caller of the registration helper FUN_140ba8370 is the BindCommandToLua binding itself).
--     So there is no /dofile console command at retail until something calls BindCommandToLua.
--   * The UiModules route is dead: guiParseSetupFile (0x140ba45f0) is a three-instruction stub
--     whose body is "mov dword ptr ds:[0], 1" - a deliberate crash.
--
-- What DOES load it, and the only thing that does without touching a shipped file:
--
--   THE CLIENT'S OWN DEBUG CONSOLE EVALUATES A TYPED LINE AS LUA SOURCE.
--   FUN_140ba93a0(ctx, wchar_t* line) builds
--       "evalStat, evalResult = pcall( function() return " .. line:sub(2) .. " end )"   if line[1] == '='
--       "evalStat, evalResult = pcall( function() " .. line .. " return 1 end )"        otherwise
--   runs it, and prints "Lua error: %s" through the ChatHandler PrintConsole slot (DAT_143f69f30
--   vt+0x18) - the same slot the server's 06 03 lines come out of.
--
--   So the one-time bootstrap, typed by the owner at the Tilde console, is exactly one line:
--
--       dofile(Client.PathScripts .. "CranberryMenu.lua")
--
--   and to see the answer instead of silence:
--
--       =dofile(Client.PathScripts .. "CranberryMenu.lua")
--
--   It must be re-typed after every client restart. CranberryMenu.Reload() re-runs it from inside
--   Lua once the file is loaded once, so the server can hot-reload this file with /win raw
--   CranberryMenu.Reload without the owner touching the keyboard.
--
--   [I], not [P]: that FUN_140ba93a0's single caller is the debug console's submit handler is
--   inferred from three things - it takes a UTF-16 string (Scaleform TextInput hands back UTF-16),
--   it prints through the console's own print slot, and it uses Lua's interactive "=expr"
--   convention. It has one caller and no other entry point in the image. The probe in
--   out\devconsole-20260901\lua-menu-probe.md settles it in one keystroke.
--------------------------------------------------------------------------------------------------

-- One table, one global. 1a 07 needs _G["CranberryMenu"] to be a LUA_TTABLE and the field to be a
-- LUA_TFUNCTION (FUN_140ba89e0); the lookup is lua_gettable, so __index inheritance works, which is
-- why inheritsFrom(UiHandlerBase) below stays reachable from the wire.
CranberryMenu = CranberryMenu or {}

local M = CranberryMenu

M.VERSION      = "1.0.0"
M.SENTINEL     = "CRANBERRY:"          -- the server's 06 03 prefix this file consumes
M.WOD_TABLE    = "CRANBERRY"           -- the first string of the outbound WallOfData 9a 05
M.TITLE        = "CRANBERRY"
M.SUBTITLE     = "Cranberry dev menu"

-- Presentation, Jiggy-flavoured (R4 section 1.1): a tinted column, a cyan highlight bar, [ON]/[OFF]
-- in green/red. These are HTML colours because a CLIK TextArea/Label honours <font color=...>.
M.COL_TEXT     = "#D8D8D8"
M.COL_TITLE    = "#FFFFFF"
M.COL_CURSOR   = "#66E0FF"   -- the highlight row
M.COL_DIM      = "#8A8A8A"
M.COL_ON       = "#5CE05C"
M.COL_OFF      = "#E05C5C"
M.COL_RULE     = "#5A3E8C"   -- Jiggy's purple column edge
M.FONT         = "$MainFontMono"
M.FONT_SIZE    = 14

--------------------------------------------------------------------------------------------------
-- 0. tiny helpers (Lua 5.1 only; no goto, no #! , no 5.2 idioms)
--------------------------------------------------------------------------------------------------

local function isFn(v) return type(v) == "function" end
local function has(t, k) return type(t) == "table" and t[k] ~= nil end

-- Never let anything in this file throw into the C++ that called us: a raised error inside the
-- OnPrintConsole hook would surface as "Lua error:" on every single server console line.
local function guard(fn, ...)
    if not isFn(fn) then return false, "not a function" end
    return pcall(fn, ...)
end

local function say(text)
    if isFn(print) then print(tostring(text)) end
end

M.log = {}          -- the last N loose server lines, for the strip under the menu
M.LOG_MAX = 6

local function pushLog(line)
    table.insert(M.log, line)
    while table.getn(M.log) > M.LOG_MAX do table.remove(M.log, 1) end
end

-- Ui.MakeHtmlSafe is a native (R6 section 1.7); fall back to a hand-rolled escape if it is missing
-- so this file still renders on a build that does not have it.
local function htmlSafe(s)
    s = tostring(s or "")
    if has(Ui, "MakeHtmlSafe") then
        local ok, out = pcall(Ui.MakeHtmlSafe, s)
        if ok and type(out) == "string" then return out end
    end
    s = string.gsub(s, "&", "&amp;")
    s = string.gsub(s, "<", "&lt;")
    s = string.gsub(s, ">", "&gt;")
    return s
end

-- A monospace TextArea collapses runs of spaces in HTML mode, and the whole frame is a fixed grid,
-- so every space becomes a non-breaking space. This is why the server may keep sending the exact
-- same ASCII frame it sends to the text surface.
local function nbsp(s)
    return (string.gsub(s, " ", "&nbsp;"))
end

--------------------------------------------------------------------------------------------------
-- 1. the window
--
-- Route 2 of R6 section 3.4: UiHandlerBase:SetUiProperties -> Window.Create(wndName, swfName,
-- swfFile, tableName, ps4SwfFile). No XML, no pack, no new .gfx - we borrow a movie that already
-- ships. MOTDWidget is first because it is the only shipped movie R6 proved carries the full CLIK
-- list stack (DataProvider, Label with htmlText, ScrollBar, ButtonGroup, NavigationCode,
-- InputDelegate, FocusHandler); ConsoleWindow is second because its TextArea has appendHtml.
--------------------------------------------------------------------------------------------------

M.MOVIES = {
    "UI\\MOTDWidget.swf",
    "UI\\ConsoleWindow.swf",
    "UI\\HudSystemMessagesWindow.swf",
    "UI\\ConfirmationDialog.swf",
}

-- Which AS3 entry point actually takes our HTML is [U] until the first click: Invoke returns
-- nothing and a wrong name is silent on screen. So we call the whole ladder on every paint. A wrong
-- name costs one no-op Invoke and - usefully - one line in Client\Logs\GFxWrap.log, which is how
-- the owner finds out which one is real. Once he knows, the server pins it with the M| frame
-- command (or he calls CranberryMenu.SetMethod from the console) and the ladder collapses to one.
M.METHODS = {
    "setText", "setHtmlText", "setMotdText", "setMOTD", "updateText",
    "setOutputMessage", "appendHtml", "setData", "setBodyText", "setMessage",
}
M.pinnedMethod = nil

M.wndName  = "Main.wndCranberryMenu"
M.swfShort = "swfCranberryMenu"
M.created  = false
M.movie    = nil
M.isShown  = false

local function haveUiLayer()
    return isFn(inheritsFrom) and type(UiHandlerBase) == "table" and type(Window) == "table"
end

--- Builds the window once. Safe to call repeatedly.
-- @return true when Window.Create ran, false and a reason otherwise.
function M.Create()
    if M.created then return true, M.movie end
    if not haveUiLayer() then
        return false, "no UiHandlerBase/Window in _G - ScriptsBase.bin is not in this Lua state"
    end

    -- Inherit so that Show/Hide/ASInvoke/SetDesiredKeys/OnUserEvent come from UiHandlerBase and are
    -- reachable through lua_gettable's __index walk, exactly as Console does (R6 section 1.2).
    --
    -- inheritsFrom (proto[0,10,1]) builds a fresh class table, gives it create/class/superClass/
    -- instanceof, and does setmetatable(newClass, {__index = baseClass}). We take its own keys and
    -- its metatable onto CranberryMenu rather than returning a new table, because the global name
    -- CranberryMenu must stay the same object the 1a 07 invoker already resolved.
    local base = inheritsFrom(UiHandlerBase)
    for k, v in pairs(base) do
        if M[k] == nil then M[k] = v end
    end
    setmetatable(M, getmetatable(base))
    M.superIndex = UiHandlerBase
    M.keyboardKeys = M.keyboardKeys or {}

    local layer = (type(UiZLayers) == "table" and (UiZLayers.TOOL or UiZLayers.OVERLAY_WINDOWS)) or nil

    local lastErr
    for i = 1, table.getn(M.MOVIES) do
        local file = M.MOVIES[i]
        local ok, err = pcall(function()
            M:SetUiProperties(M.wndName, M.swfShort, file, "CranberryMenu", layer)
        end)
        if ok then
            M.movie = file
            M.created = true
            say("CranberryMenu: window '" .. M.wndName .. "' created over " .. file)
            -- Ask for the arrow keys. Whether the borrowed movie ever raises them back at us is
            -- [U] (R6 section 4.3) - this costs nothing and is the half we can do from Lua.
            M.WantKeys()
            if has(Window, "SetCaption") then
                pcall(Window.SetCaption, M.wndName, M.TITLE .. " " .. M.VERSION)
            end
            return true, file
        end
        lastErr = err
    end

    return false, "Window.Create failed for every candidate movie: " .. tostring(lastErr)
end

--- Routes the navigation keys to this window rather than to the game.
function M.WantKeys()
    if type(KeyCodes) ~= "table" then return false, "no KeyCodes table" end
    local keys = {}
    local wanted = {
        "cKeyUp", "cKeyDown", "cKeyLeft", "cKeyRight",
        "cKeyEnter", "cKeyReturn", "cKeyEscape", "cKeyBack", "cKeyBackspace",
    }
    for i = 1, table.getn(wanted) do
        local k = KeyCodes[wanted[i]]
        if k ~= nil then table.insert(keys, k) end
    end
    M.keyboardKeys = keys
    local ok = guard(function()
        if isFn(M.SetDesiredKeys) then M:SetDesiredKeys(unpack(keys)) end
        if isFn(M.SetKeyboardEnabled) then M:SetKeyboardEnabled(true) end
    end)
    return ok, table.getn(keys)
end

--------------------------------------------------------------------------------------------------
-- 2. painting
--------------------------------------------------------------------------------------------------

M.rows  = {}        -- the frame the server last sent, one ASCII line per entry
M.dirty = false

--- Turns one server ASCII row into one styled HTML line.
-- The server keeps sending the SAME grid FrameRenderer already produces (design section 2.7), so
-- there is exactly one renderer on the server and this file only re-colours it:
--   '+===+' / '+---+'  the frame rules      -> the purple column edge
--   '|  > 3 Label  v |' a row, '>' at col 3 -> the cyan highlight bar
--   '[ON]' / '[OFF]'                        -> green / red
local function styleRow(raw)
    local body = raw
    local first = string.sub(body, 1, 1)

    -- a rule line
    if string.find(body, "^[%+#][%-=]+[%+#]$") then
        return "<font color='" .. M.COL_RULE .. "'>" .. nbsp(htmlSafe(body)) .. "</font>"
    end

    local cursor = (string.sub(body, 3, 3) == ">")
    local colour = cursor and M.COL_CURSOR or M.COL_TEXT
    local safe = nbsp(htmlSafe(body))

    -- [ON] / [OFF] keep their own colour even on the highlighted row.
    safe = string.gsub(safe, "%[ON%]", "<font color='" .. M.COL_ON .. "'>[ON]</font>")
    safe = string.gsub(safe, "%[OFF%]", "<font color='" .. M.COL_OFF .. "'>[OFF]</font>")

    if cursor then
        return "<font color='" .. colour .. "'><b>" .. safe .. "</b></font>"
    end
    if first == "|" or first == "#" then
        return "<font color='" .. colour .. "'>" .. safe .. "</font>"
    end
    return "<font color='" .. M.COL_DIM .. "'>" .. safe .. "</font>"
end

--- Builds the whole frame as one HTML string.
function M.Html()
    local out = {}
    table.insert(out, "<p align='left'><font face='" .. M.FONT .. "' size='" .. M.FONT_SIZE
        .. "' color='" .. M.COL_TITLE .. "'><b>" .. htmlSafe(M.TITLE) .. "</b>  <font color='"
        .. M.COL_DIM .. "'>" .. htmlSafe(M.SUBTITLE) .. "</font></font></p>")
    table.insert(out, "<p align='left'><font face='" .. M.FONT .. "' size='" .. M.FONT_SIZE .. "'>")
    for i = 1, table.getn(M.rows) do
        table.insert(out, styleRow(M.rows[i]))
        table.insert(out, "<br/>")
    end
    if table.getn(M.log) > 0 then
        table.insert(out, "<br/><font color='" .. M.COL_DIM .. "'>")
        for i = 1, table.getn(M.log) do
            table.insert(out, nbsp(htmlSafe(M.log[i])) .. "<br/>")
        end
        table.insert(out, "</font>")
    end
    table.insert(out, "</font></p>")
    return table.concat(out)
end

--- Pushes the current frame into the movie.
function M.Paint()
    M.dirty = false
    if not M.created then
        local ok = M.Create()
        if not ok then return false end
    end

    local html = M.Html()
    local sent = 0

    if M.pinnedMethod then
        guard(function() M:ASInvoke(M.pinnedMethod, html) end)
        return true
    end

    for i = 1, table.getn(M.METHODS) do
        local ok = guard(function() M:ASInvoke(M.METHODS[i], html) end)
        if ok then sent = sent + 1 end
    end

    -- Window.SetCaption is native and takes a string, so the title bar is a second, independent
    -- proof surface: if the caption changes and the body does not, the movie loaded and the AS3
    -- method name is the thing that is wrong.
    if has(Window, "SetCaption") then
        pcall(Window.SetCaption, M.wndName, M.TITLE .. "  " .. tostring(table.getn(M.rows)) .. " rows")
    end
    return sent > 0
end

--- Pins the one AS3 method that actually worked, so the ladder stops firing.
function M.SetMethod(name)
    if type(name) == "string" and name ~= "" then
        M.pinnedMethod = name
        say("CranberryMenu: AS3 method pinned to '" .. name .. "'")
    else
        M.pinnedMethod = nil
        say("CranberryMenu: AS3 method ladder re-armed")
    end
    return true
end

--------------------------------------------------------------------------------------------------
-- 3. the server -> Lua string channel: the OnPrintConsole hook
--
-- OnPrintConsole is a bare Lua GLOBAL (SETGLOBAL in proto[0,11]; R6 section 1.6 corrects R2 which
-- called it a ChatHandler method). The C++ PrintConsole slot FUN_141250c70 calls it with the text
-- of the server's Chat 06 03 00 packet. Reassigning it here is the whole server->script pipe: no
-- new packet, no protocol change, and a frame line never reaches the console pane.
--------------------------------------------------------------------------------------------------

M.baseOnPrintConsole = M.baseOnPrintConsole or OnPrintConsole
M.frames = 0
M.linesConsumed = 0

--- Handles one already-stripped frame command. Returns nothing; never throws.
function M.Feed(payload)
    M.linesConsumed = M.linesConsumed + 1

    local cmd = string.sub(payload, 1, 1)
    local rest = string.sub(payload, 3)          -- payload is "<cmd>|<text>"

    if cmd == "B" then
        M.rows = {}
        M.dirty = true
    elseif cmd == "R" then
        table.insert(M.rows, rest)
        M.dirty = true
    elseif cmd == "+" then
        local n = table.getn(M.rows)
        if n > 0 then M.rows[n] = M.rows[n] .. rest else table.insert(M.rows, rest) end
        M.dirty = true
    elseif cmd == "E" then
        M.frames = M.frames + 1
        M.Show()
        M.Paint()
    elseif cmd == "L" then
        pushLog(rest)
        M.dirty = true
        if M.isShown then M.Paint() end
    elseif cmd == "O" then
        M.Show()
        M.Paint()
    elseif cmd == "X" then
        M.Hide()
    elseif cmd == "T" then
        M.TITLE = rest
    elseif cmd == "M" then
        M.SetMethod(rest)
    elseif cmd == "P" then
        -- an echo the owner asked for: send it back out through the ORIGINAL handler so it lands in
        -- the console pane like any other server line.
        if isFn(M.baseOnPrintConsole) then
            guard(M.baseOnPrintConsole, "CranberryMenu " .. M.VERSION .. ": " .. rest, false)
        else
            say("CranberryMenu " .. M.VERSION .. ": " .. rest)
        end
    else
        pushLog("?" .. payload)
    end
end

--- Installs the hook. Idempotent: re-running this file never chains two hooks.
function M.Hook()
    local slen = string.len(M.SENTINEL)
    local base = M.baseOnPrintConsole

    OnPrintConsole = function(text, isInput)
        if type(text) == "string" and string.sub(text, 1, slen) == M.SENTINEL then
            guard(M.Feed, string.sub(text, slen + 1))
            return
        end
        if isFn(base) then return base(text, isInput) end
    end

    M.hooked = true
    return true
end

--------------------------------------------------------------------------------------------------
-- 4. Lua -> server: the reply channel
--
-- Ui.SetWallOfData(a, b) is the native the client's own UiHandlerBase:LogShow/:LogHide already use;
-- on the wire it is WallOfData 9a 05 | String8 | String8 | u32 0, which ZoneService already parses
-- into (window, action). We send window = "CRANBERRY" and action = the navigation token, and the
-- server routes it into the same MenuVerb alphabet the typed /d /u /s /b reduce to.
--
-- Ui.ProcessChatCommand("/d") is the belt-and-braces path: it makes the client type the command on
-- the player's behalf, which arrives as the Command.ExecuteCommand 09 42 the console already
-- answers. It is used automatically when SetWallOfData is missing.
--------------------------------------------------------------------------------------------------

M.wodSends  = 0
M.chatSends = 0
M.useChat   = false      -- flip with CranberryMenu.UseChat() if 9a 05 is ever not routed

function M.Send(token)
    token = tostring(token or "")
    if token == "" then return false end

    if not M.useChat and has(Ui, "SetWallOfData") then
        local ok = guard(Ui.SetWallOfData, M.WOD_TABLE, token)
        if ok then
            M.wodSends = M.wodSends + 1
            return true
        end
    end

    if has(Ui, "ProcessChatCommand") then
        local line = "/" .. token
        local ok = guard(Ui.ProcessChatCommand, line)
        if ok then
            M.chatSends = M.chatSends + 1
            return true
        end
    end

    return false
end

function M.UseChat() M.useChat = true; return true end
function M.UseWallOfData() M.useChat = false; return true end

--------------------------------------------------------------------------------------------------
-- 5. the zero-argument globals the server's 1a 07 can reach
--
-- Every one of these is CranberryMenu.<Name> with signature (self) - exactly the shape
-- Ui.ExecuteScript needs (R6 section 1.5). /win menu, /win menuon, /win menuoff and
-- /win raw CranberryMenu.<Name> all land here.
--------------------------------------------------------------------------------------------------

-- UiHandlerBase:Show / :Hide are inherited, and M.Show / M.Hide below shadow them, so the base
-- versions are reached through the class we inherited from rather than through M.
local function super(name)
    local from = M.superIndex or UiHandlerBase
    local fn = type(from) == "table" and from[name] or nil
    return isFn(fn) and fn or nil
end

function M.Show()
    if not M.created then M.Create() end
    local fn = super("Show")
    if fn then guard(fn, M) end
    M.isShown = true
    return true
end

function M.Hide()
    local fn = super("Hide")
    if fn then guard(fn, M) end
    M.isShown = false
    return true
end

--- Opens the menu: builds the window, shows it, and asks the server for a frame.
function M.Open()
    M.Create()
    M.Show()
    M.Paint()
    M.Send("m")
    return true
end

--- Closes the menu and tells the server to stop drawing.
function M.Close()
    M.Hide()
    M.Send("q")
    return true
end

function M.Toggle()
    if M.isShown then return M.Close() end
    return M.Open()
end

-- Navigation, both as things the server can press (1a 07) and as what a key would call.
function M.Up()      M.Send("u"); return true end
function M.Down()    M.Send("d"); return true end
function M.Select()  M.Send("s"); return true end
function M.Back()    M.Send("b"); return true end
function M.Redraw()  M.Send("r"); return true end

--- The negative-control-proof "we are alive" method (R6 section 5.5 D2).
function M.Ping()
    local line = "CranberryMenu " .. M.VERSION
        .. " alive: hooked=" .. tostring(M.hooked == true)
        .. " created=" .. tostring(M.created)
        .. " movie=" .. tostring(M.movie)
        .. " frames=" .. tostring(M.frames)
        .. " rows=" .. tostring(table.getn(M.rows))
        .. " wod=" .. tostring(M.wodSends)
        .. " chat=" .. tostring(M.chatSends)
    say(line)
    if isFn(M.baseOnPrintConsole) then guard(M.baseOnPrintConsole, line, false) end
    return true
end

--- Prints what this file can and cannot see, so a failed click has an answer in one line each.
function M.Diag()
    local function mark(name, v) say(string.format("  %-24s %s", name, tostring(v ~= nil))) end
    say("CranberryMenu " .. M.VERSION .. " diagnostics")
    mark("_G.inheritsFrom", inheritsFrom)
    mark("_G.UiHandlerBase", UiHandlerBase)
    mark("_G.UiZLayers", UiZLayers)
    mark("_G.Window", Window)
    mark("_G.GfxCtrl", GfxCtrl)
    mark("_G.Ui", Ui)
    mark("Ui.SetWallOfData", Ui and Ui.SetWallOfData)
    mark("Ui.ProcessChatCommand", Ui and Ui.ProcessChatCommand)
    mark("Ui.MakeHtmlSafe", Ui and Ui.MakeHtmlSafe)
    mark("_G.KeyCodes", KeyCodes)
    mark("_G.BindCommandToLua", BindCommandToLua)
    mark("_G.dofile", dofile)
    mark("Client.PathScripts", Client and Client.PathScripts)
    say("  window created=" .. tostring(M.created) .. " movie=" .. tostring(M.movie))
    say("  hook installed=" .. tostring(M.hooked == true)
        .. " lines consumed=" .. tostring(M.linesConsumed)
        .. " frames=" .. tostring(M.frames))
    return true
end

--- Re-runs this file from disk. Lets the server hot-reload the client half with
--- /win raw CranberryMenu.Reload once it has been loaded by hand a single time.
function M.Reload()
    local path = (Client and Client.PathScripts or ".\\Resources\\Scripts\\") .. "CranberryMenu.lua"
    local ok, err = pcall(dofile, path)
    say("CranberryMenu: reload " .. path .. " -> " .. tostring(ok) .. " " .. tostring(err or ""))
    return true
end

--------------------------------------------------------------------------------------------------
-- 6. keys, best effort (R6 section 4.3)
--
-- Honest status: a BORROWED movie receives arrow keys and its own CLIK components react to them,
-- but it has no code that calls back out to our Lua handler, and this build ships no loose
-- CommandHotkeys.xml (InputProfile_Default.xml / InputProfile_User.xml bind named ACTIONS, and
-- carry no commandstring attribute anywhere). So key capture is [U] until either
--   (a) the movie turns out to raise a user event we can catch - the three hooks below - or
--   (b) option (b) of R6 ships a movie of ours that does
--       ExternalInterface.call("CallLua", "CranberryMenu:OnKey", "up").
--
-- Everything below costs nothing and catches (a) if it is there.
--------------------------------------------------------------------------------------------------

M.KEYMAP = {
    up = "u", down = "d", left = "b", right = "s",
    enter = "s", ["return"] = "s", escape = "q", back = "b", backspace = "b",
    pageup = "uu", pagedown = "dd",
}

--- The one entry point every key route funnels into. Takes a NAME, not a keycode.
function M.OnKey(name)
    local token = M.KEYMAP[string.lower(tostring(name or ""))]
    if not token then return false end
    return M.Send(token)
end

-- UiHandlerBase:OnUserEvent(name, ...) does self[name](self, ...), so a movie that raises
-- "onKeyUp" reaches CranberryMenu:onKeyUp. Defined for every navigation name the CLIK
-- NavigationCode constants use.
function M:onKeyUp()        return M.OnKey("up")     end
function M:onKeyDown()      return M.OnKey("down")   end
function M:onKeyLeft()      return M.OnKey("left")   end
function M:onKeyRight()     return M.OnKey("right")  end
function M:onKeyEnter()     return M.OnKey("enter")  end
function M:onKeyEscape()    return M.OnKey("escape") end
function M:onKeyBack()      return M.OnKey("back")   end

-- The shape InGameBrowserHandler:OnKeyEvent(a,b,c,d) uses - the one handler in ScriptsBase.bin that
-- receives keys this way. We take the first argument as a key name or a KeyCodes value.
function M:OnKeyEvent(a)
    if type(a) == "string" then return M.OnKey(a) end
    if type(a) == "number" and type(KeyCodes) == "table" then
        for name, code in pairs(KeyCodes) do
            if code == a then
                return M.OnKey(string.gsub(string.lower(name), "^ckey", ""))
            end
        end
    end
    return false
end

--- Registers short CLIENT console commands that reach this file directly, with no server hop.
-- BindCommandToLua(commandName, luaGlobalName) is a native global (FUN_140ba50a0). The dispatcher
-- FUN_140bad090 then compiles "return function() <luaGlobalName>([[<typed tail>]]) end" - so a
-- command with no tail calls the global with no arguments. Nothing in the shipped scripts calls
-- BindCommandToLua, so this is the first thing on this client that ever fills that table.
--
-- After this runs, the owner can type   cmd   cmu   cms   cmb   cmm   at the Tilde console.
function M.BindKeys()
    if not isFn(BindCommandToLua) then return false, "no BindCommandToLua in _G" end

    CranberryMenuUp     = function() return M.OnKey("up")    end
    CranberryMenuDown   = function() return M.OnKey("down")  end
    CranberryMenuSelect = function() return M.OnKey("enter") end
    CranberryMenuBack   = function() return M.OnKey("back")  end
    CranberryMenuToggle = function() return M.Toggle()       end

    local pairsToBind = {
        { "cmu", "CranberryMenuUp" },
        { "cmd", "CranberryMenuDown" },
        { "cms", "CranberryMenuSelect" },
        { "cmb", "CranberryMenuBack" },
        { "cmm", "CranberryMenuToggle" },
    }
    local bound = 0
    for i = 1, table.getn(pairsToBind) do
        local ok = guard(BindCommandToLua, pairsToBind[i][1], pairsToBind[i][2])
        if ok then bound = bound + 1 end
    end
    say("CranberryMenu: bound " .. bound .. " client console commands (cmu cmd cms cmb cmm)")
    return bound > 0, bound
end

--------------------------------------------------------------------------------------------------
-- 7. load
--------------------------------------------------------------------------------------------------

M.Hook()
M.BindKeys()

-- The window is NOT created at load: Window.Create before the UI layer is up would be the one thing
-- in this file that could disturb a login. It is built lazily on the first Open/Paint, which is
-- always after a deliberate act by the server or the owner.

say("CranberryMenu " .. M.VERSION .. " loaded - OnPrintConsole hooked on '" .. M.SENTINEL
    .. "'. Try CranberryMenu.Ping() then CranberryMenu.Open(), or /surface lua then /m on the server.")

return M
