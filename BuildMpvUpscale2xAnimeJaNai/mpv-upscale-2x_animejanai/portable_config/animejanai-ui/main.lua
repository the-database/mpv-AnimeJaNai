-- animejanai_ui: the mpv-animejanai player's interface. Loaded only by the mpv-animejanai launcher
-- (--script=<config>/animejanai-ui); plain mpv runs without it.
--
-- Script messages (script-message-to animejanai_ui <name> ...), used by menu.conf and the bindings:
--   open-files, add-files, load-audio, load-sub, open-disc   native file dialogs (the launcher's
--                                                             --open-dialog), then load the result
--   open-clipboard          load the URLs / file paths on the clipboard
--   open-url <url>, open-config-folder                       open with the system's default program
--   select-profile          pick a profile to apply
--   quick-bookmark          first press marks the position, the next one returns to it
--   register video|audio|image|unreg|path|unpath            file associations and PATH (the launcher's
--                                                             --register / --unregister)
--
-- Also remembers volume, mute, audio device and the window's size and position (position on
-- Windows only: mpv has no window-position property) in ~~/animejanai-ui-state.json, importing
-- them once from mpv.net's settings.xml.
--
-- Options (script-opts animejanai_ui-<name>): launcher (set by the launcher), remember_volume,
-- remember_window (yes/no).

local mp = require "mp"
local msg = require "mp.msg"
local utils = require "mp.utils"
local options = require "mp.options"
local input = require "mp.input"

local o = {
    launcher = "",
    remember_volume = true,
    remember_window = true,
}
options.read_options(o, "animejanai_ui")

local platform = mp.get_property("platform")
local config_dir = mp.command_native({ "expand-path", "~~/" })
local state_path = utils.join_path(config_dir, "animejanai-ui-state.json")

local function osd(text, seconds)
    mp.osd_message(text, seconds or 3)
    -- Tests read the last message back over IPC.
    mp.set_property("user-data/animejanai_ui/last-message", text)
end

local function exists(path)
    return path and utils.file_info(path) ~= nil
end

local function is_dir(path)
    local info = path and utils.file_info(path)
    return info ~= nil and info.is_dir
end

-- ---- helper programs ----------------------------------------------------------------------------

-- Helper programs other than the launcher (the browser, the file manager) must not see the
-- bundled libraries the Linux launcher puts on LD_LIBRARY_PATH.
local function helper_env()
    local orig = os.getenv("ANIMEJANAI_ORIG_LD_LIBRARY_PATH")
    if platform == "windows" or orig == nil then
        return nil
    end
    local env = {}
    for _, kv in ipairs(utils.get_env_list()) do
        if not kv:match("^LD_LIBRARY_PATH=") and not kv:match("^ANIMEJANAI_ORIG_LD_LIBRARY_PATH=") then
            env[#env + 1] = kv
        end
    end
    if orig ~= "" then
        env[#env + 1] = "LD_LIBRARY_PATH=" .. orig
    end
    return env
end

-- Opens a URL, file or folder with the system's default program.
local function open_external(target)
    local args
    if platform == "windows" then
        args = { "explorer.exe", target }
    elseif platform == "darwin" then
        args = { "open", target }
    else
        args = { "xdg-open", target }
    end
    mp.command_native_async({ name = "subprocess", args = args, detach = true, playback_only = false,
                              env = helper_env() }, function() end)
end

local function launcher_missing()
    if o.launcher == "" or not exists(o.launcher) then
        osd("This needs the mpv-animejanai launcher (start the player through it).")
        return true
    end
    return false
end

-- Runs the launcher's native file picker without blocking the player; calls done(paths) with the
-- chosen paths (never called when cancelled).
local function pick(opts, done)
    if launcher_missing() then
        return
    end
    local args = { o.launcher, "--open-dialog", "--owner", tostring(mp.get_property_number("window-id", 0)),
                   "--title", opts.title }
    if opts.multi then args[#args + 1] = "--multi" end
    if opts.folder then args[#args + 1] = "--folder" end
    if opts.filter then
        args[#args + 1] = "--filter"
        args[#args + 1] = opts.filter
    end
    local path = mp.get_property("path")
    if path and not path:find("://") then
        local dir = utils.split_path(utils.join_path(utils.getcwd() or "", path))
        if is_dir(dir) then
            args[#args + 1] = "--initial"
            args[#args + 1] = dir
        end
    end
    mp.command_native_async({ name = "subprocess", args = args, capture_stdout = true, capture_stderr = true,
                              playback_only = false }, function(ok, res)
        local status = ok and res and res.status or -1
        if status == 1 then
            return -- cancelled
        elseif status == 3 then
            osd("No file dialog available: install zenity or kdialog.", 5)
            return
        elseif status ~= 0 then
            msg.error("file dialog failed: " .. tostring(res and (res.stderr or res.error_string)))
            osd("The file dialog failed (see the log).")
            return
        end
        local paths = {}
        for line in (res.stdout or ""):gmatch("[^\r\n]+") do
            paths[#paths + 1] = line
        end
        if #paths > 0 then
            done(paths)
        end
    end)
end

local function patterns(list)
    local out = {}
    for _, ext in ipairs(list or {}) do
        out[#out + 1] = "*." .. ext
    end
    return table.concat(out, ";")
end

local function exts(option, fallback)
    local list = mp.get_property_native(option)
    if type(list) ~= "table" or #list == 0 then
        return fallback
    end
    return list
end

local function media_filter()
    local video = patterns(exts("video-exts", { "mkv", "mp4", "avi", "webm", "mov", "ts", "m2ts" }))
    local audio = patterns(exts("audio-exts", { "flac", "mp3", "m4a", "opus", "ogg", "wav" }))
    local image = patterns(exts("image-exts", { "png", "jpg", "jpeg", "webp" }))
    return "Media files|" .. video .. ";" .. audio .. ";" .. image ..
           "|Video files|" .. video .. "|Audio files|" .. audio .. "|All files|*.*"
end

-- ---- opening things ------------------------------------------------------------------------------

local function load_list(paths, append)
    for i, p in ipairs(paths) do
        mp.commandv("loadfile", p, (i == 1 and not append) and "replace" or "append-play")
    end
end

mp.register_script_message("open-files", function()
    pick({ title = "Open Files", multi = true, filter = media_filter() }, function(paths)
        load_list(paths, false)
    end)
end)

mp.register_script_message("add-files", function()
    pick({ title = "Add Files to Playlist", multi = true, filter = media_filter() }, function(paths)
        load_list(paths, true)
        osd(#paths == 1 and "Added 1 file to the playlist" or ("Added " .. #paths .. " files to the playlist"))
    end)
end)

mp.register_script_message("load-audio", function()
    local filter = "Audio files|" .. patterns(exts("audio-exts", { "flac", "mp3", "m4a", "opus", "ac3", "dts" })) .. "|All files|*.*"
    pick({ title = "Load External Audio", multi = true, filter = filter }, function(paths)
        for _, p in ipairs(paths) do
            mp.commandv("audio-add", p)
        end
    end)
end)

mp.register_script_message("load-sub", function()
    local filter = "Subtitle files|" .. patterns(exts("sub-auto-exts", { "ass", "srt", "ssa", "sub", "sup", "vtt", "idx", "smi" })) .. "|All files|*.*"
    pick({ title = "Load External Subtitles", multi = true, filter = filter }, function(paths)
        for _, p in ipairs(paths) do
            mp.commandv("sub-add", p)
        end
    end)
end)

-- A Blu-ray or DVD folder (or drive): BDMV means Blu-ray, anything else is tried as a DVD.
mp.register_script_message("open-disc", function()
    pick({ title = "Open DVD/Blu-ray Drive or Folder", folder = true }, function(paths)
        local dir = paths[1]:gsub("[/\\]+$", "")
        local parent, name = utils.split_path(dir)
        parent = parent:gsub("[/\\]+$", "")
        if name and name:upper() == "BDMV" then
            dir = parent
        elseif name and name:upper() == "VIDEO_TS" then
            dir = parent
        end
        if is_dir(utils.join_path(dir, "BDMV")) then
            mp.set_property("bluray-device", dir)
            mp.commandv("loadfile", "bd://")
        else
            mp.set_property("dvd-device", dir)
            mp.commandv("loadfile", "dvd://")
        end
    end)
end)

-- URLs and existing files, one per line; anything else on the clipboard is ignored.
mp.register_script_message("open-clipboard", function(mode)
    local text = mp.get_property("clipboard/text") or ""
    local items = {}
    for line in text:gmatch("[^\r\n]+") do
        line = line:match("^%s*\"?(.-)\"?%s*$")
        if line:find("://") or exists(line) then
            items[#items + 1] = line
        end
    end
    if #items == 0 then
        osd("The clipboard has no URL or file path.")
        return
    end
    load_list(items, mode == "append")
end)

mp.register_script_message("open-url", function(url)
    if url and url ~= "" then
        open_external(url)
    end
end)

mp.register_script_message("open-config-folder", function()
    open_external(config_dir)
end)

-- ---- profiles, bookmarks, registration -----------------------------------------------------------

mp.register_script_message("select-profile", function()
    local names = {}
    for _, p in ipairs(mp.get_property_native("profile-list") or {}) do
        if p.name ~= "default" and not p.name:match("^builtin%-") then
            names[#names + 1] = p.name
        end
    end
    table.sort(names)
    input.select({
        prompt = "Apply profile:",
        items = names,
        submit = function(i)
            mp.commandv("apply-profile", names[i])
            osd("Profile: " .. names[i])
        end,
    })
end)

local bookmarks = {}

mp.register_script_message("quick-bookmark", function()
    local path = mp.get_property("path")
    local pos = mp.get_property_number("time-pos")
    if not path or not pos then
        return
    end
    if bookmarks[path] then
        local back = bookmarks[path]
        bookmarks[path] = nil
        mp.commandv("seek", tostring(back), "absolute+exact")
        osd("Back to the bookmark at " .. mp.format_time(back))
    else
        bookmarks[path] = pos
        osd("Bookmark set at " .. mp.format_time(pos) .. " (press again to return)")
    end
end)

mp.register_script_message("register", function(kind)
    if launcher_missing() then
        return
    end
    local args
    if platform ~= "windows" then
        -- Linux: one desktop entry (menu entry, "Open with", default player) covers every kind.
        if kind == "path" or kind == "unpath" then
            osd("PATH registration is Windows-only (the install's launcher can be linked into ~/.local/bin).")
            return
        end
        args = kind == "unreg" and { o.launcher, "--unregister", "desktop" }
                                or { o.launcher, "--register", "desktop", "--default" }
    elseif kind == "unreg" then
        args = { o.launcher, "--unregister", "all" }
    elseif kind == "unpath" then
        args = { o.launcher, "--unregister", "path" }
    elseif kind == "path" then
        args = { o.launcher, "--register", "path" }
    else
        args = { o.launcher, "--register", kind, "--default" }
    end
    mp.command_native_async({ name = "subprocess", args = args, capture_stderr = true, playback_only = false },
        function(ok, res)
            if not ok or not res or res.status ~= 0 then
                msg.error("registration failed: " .. tostring(res and (res.stderr or res.error_string)))
                osd("Registration failed (see the log).")
            elseif kind == "unreg" then
                osd("File associations removed.")
            elseif kind == "path" or kind == "unpath" then
                osd(kind == "path" and "Added to PATH (new terminals see it)." or "Removed from PATH.")
            else
                osd("Registered for " .. kind .. " files. To make it the default player, pick mpv-AnimeJaNai in the system's default apps settings.", 6)
            end
        end)
end)

-- ---- remembered state -----------------------------------------------------------------------------

local function read_state()
    local f = io.open(state_path, "r")
    if not f then
        return nil
    end
    local text = f:read("*a")
    f:close()
    local state = utils.parse_json(text)
    return type(state) == "table" and state or nil
end

-- mpv.net kept these in portable_config/settings.xml: carried over once, on the first run.
local function import_mpvnet()
    local f = io.open(utils.join_path(config_dir, "settings.xml"), "r")
    if not f then
        return nil
    end
    local xml = f:read("*a")
    f:close()
    local state = {}
    state.volume = tonumber(xml:match("<Volume>%s*([%d%.]+)%s*</Volume>"))
    local mute = xml:match("<Mute>%s*(%a+)%s*</Mute>")
    if mute then
        state.mute = mute == "yes"
    end
    state.audio_device = xml:match("<AudioDevice>%s*(.-)%s*</AudioDevice>")
    local size = xml:match("<WindowSize>(.-)</WindowSize>")
    if size then
        local w = tonumber(size:match("<Width>%s*(%d+)"))
        local h = tonumber(size:match("<Height>%s*(%d+)"))
        if w and h and w > 0 and h > 0 then
            state.width, state.height = w, h
        end
    end
    msg.info("imported volume and window settings from mpv.net's settings.xml")
    return state
end

local function device_available(name)
    for _, d in ipairs(mp.get_property_native("audio-device-list") or {}) do
        if d.name == name then
            return true
        end
    end
    return false
end

-- Runs while the player starts, before the window exists, so the options apply to it.
local function restore()
    local state = read_state() or import_mpvnet() or {}
    if o.remember_volume then
        if state.volume then mp.set_property_number("volume", state.volume) end
        if state.mute ~= nil then mp.set_property_bool("mute", state.mute) end
        if state.audio_device and state.audio_device ~= "" and device_available(state.audio_device) then
            mp.set_property("audio-device", state.audio_device)
        end
    end
    -- The window: only when the user's config doesn't place or size it itself.
    local user_sized = mp.get_property("geometry", "") ~= "" or mp.get_property("autofit", "") ~= ""
                       or mp.get_property("autofit-larger", "") ~= "" or mp.get_property("autofit-smaller", "") ~= ""
    if o.remember_window and not user_sized and state.width and state.height then
        local geometry = string.format("%dx%d", state.width, state.height)
        if state.x and state.y and platform == "windows" then
            -- "+-20" is 20 pixels left of the screen's edge (mpv's X11-style syntax).
            geometry = geometry .. string.format("+%d+%d", state.x, state.y)
        end
        mp.set_property("geometry", geometry)
        if state.maximized then
            mp.set_property_bool("window-maximized", true)
        end
    end
end

local last = {}

-- The client area's size while the window is neither fullscreen nor maximized.
local function track_size()
    if mp.get_property_bool("fullscreen") or mp.get_property_bool("window-maximized") then
        return
    end
    local dims = mp.get_property_native("osd-dimensions")
    if dims and dims.w and dims.w > 0 and dims.h > 0 then
        last.width, last.height = dims.w, dims.h
    end
end

-- Windows: where the window is, from the launcher (mpv has no property for it), in the form
-- --geometry takes back. The window still exists while scripts handle "shutdown".
local function window_position()
    if platform ~= "windows" or o.launcher == "" then
        return nil
    end
    local wid = mp.get_property_number("window-id")
    if not wid or mp.get_property_bool("fullscreen") or mp.get_property_bool("window-maximized") then
        return nil
    end
    local res = mp.command_native({ name = "subprocess", args = { o.launcher, "--window-rect", tostring(wid) },
                                    capture_stdout = true, playback_only = false })
    local x, y = (res and res.stdout or ""):match("^(%-?%d+) (%-?%d+)")
    return tonumber(x), tonumber(y)
end

local function save()
    local state = read_state() or {}
    if o.remember_volume then
        state.volume = mp.get_property_number("volume", state.volume)
        state.mute = mp.get_property_bool("mute", state.mute)
        state.audio_device = mp.get_property("audio-device", state.audio_device)
    end
    if o.remember_window and mp.get_property_number("window-id") then
        track_size()
        state.width, state.height = last.width or state.width, last.height or state.height
        state.maximized = mp.get_property_bool("window-maximized", false)
        local x, y = window_position()
        if x and y then
            state.x, state.y = x, y
        end
    end
    local f = io.open(state_path, "w")
    if not f then
        msg.warn("could not write " .. state_path)
        return
    end
    f:write(utils.format_json(state))
    f:close()
end

restore()
mp.observe_property("osd-dimensions", "native", track_size)
mp.register_event("shutdown", save)
mp.set_property_bool("user-data/animejanai_ui/loaded", true)
