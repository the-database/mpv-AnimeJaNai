-- Answers mpv.net's script messages (script-message-to mpvnet <command> ...) so key bindings
-- written for mpv.net, in the user's own input.conf, keep working under the mpv-animejanai player.
-- Loaded by the launcher with --script; the file name makes the script name "mpvnet".
-- Commands that have no counterpart say so on screen.

local mp = require "mp"

local function ui(...)
    mp.commandv("script-message-to", "animejanai_ui", ...)
end

local function binding(name)
    return function()
        mp.commandv("script-binding", name)
    end
end

local function unavailable(name)
    return function()
        mp.osd_message("The mpv.net command \"" .. name .. "\" is not available in this player.", 4)
    end
end

local commands = {
    ["open-files"] = function(mode) ui(mode == "append" and "add-files" or "open-files") end,
    ["open-clipboard"] = function(mode) ui("open-clipboard", mode or "") end,
    ["open-optical-media"] = function() ui("open-disc") end,
    ["load-audio"] = function() ui("load-audio") end,
    ["load-sub"] = function() ui("load-sub") end,
    ["open-conf-folder"] = function() ui("open-config-folder") end,
    ["shell-execute"] = function(target) ui("open-url", target or "") end,
    ["edit-conf-file"] = function(file)
        ui("open-url", mp.command_native({ "expand-path", "~~/" .. (file or "mpv.conf") }))
    end,
    ["play-pause"] = function()
        if mp.get_property_number("playlist-count", 0) == 0 then
            ui("open-files")
        else
            mp.command("cycle pause")
        end
    end,
    ["cycle-audio"] = function() mp.command("cycle audio") end,
    ["cycle-subtitles"] = function() mp.command("cycle sub") end,
    ["playlist-add"] = function(n)
        mp.command((tonumber(n) or 1) < 0 and "playlist-prev" or "playlist-next")
    end,
    ["playlist-first"] = function() mp.set_property_number("playlist-pos", 0) end,
    ["playlist-last"] = function()
        mp.set_property_number("playlist-pos", mp.get_property_number("playlist-count", 1) - 1)
    end,
    ["playlist-random"] = function()
        local n = mp.get_property_number("playlist-count", 0)
        if n > 0 then
            mp.set_property_number("playlist-pos", math.random(0, n - 1))
        end
    end,
    ["show-progress"] = function() mp.command("show-progress") end,
    ["show-text"] = function(text, duration) mp.osd_message(text or "", (tonumber(duration) or 2000) / 1000) end,
    ["scale-window"] = function(f) mp.commandv("multiply", "current-window-scale", f or "1") end,
    ["window-scale"] = function(f) mp.commandv("set", "current-window-scale", f or "1") end,
    ["show-menu"] = binding("select/context-menu"),
    ["show-playlist"] = binding("select/select-playlist"),
    ["show-audio-tracks"] = binding("select/select-aid"),
    ["show-subtitle-tracks"] = binding("select/select-sid"),
    ["show-chapters"] = binding("select/select-chapter"),
    ["show-audio-devices"] = binding("select/select-audio-device"),
    ["show-command-palette"] = binding("select/select-binding"),
    ["show-keys"] = binding("select/select-binding"),
    ["show-bindings"] = binding("select/select-binding"),
    ["show-properties"] = binding("select/show-properties"),
    ["show-history"] = binding("select/select-watch-history"),
    ["show-recent"] = binding("select/select-watch-history"),
    ["show-info"] = binding("stats/display-stats-toggle"),
    ["show-media-info"] = binding("stats/display-stats-toggle"),
    ["show-profiles"] = function() ui("select-profile") end,
    ["select-profile"] = function() ui("select-profile") end,
    ["quick-bookmark"] = function() ui("quick-bookmark") end,
    ["show-conf-editor"] = function() mp.commandv("script-message", "animejanai-manager", "settings") end,
    ["reg-file-assoc"] = function(kind) ui("register", kind or "video") end,
    ["add-to-path"] = function() ui("register", "path") end,
    ["remove-from-path"] = function() ui("register", "unpath") end,
}

for _, name in ipairs({ "show-about", "show-decoders", "show-demuxers", "show-protocols", "show-commands",
                         "show-input-editor", "move-window" }) do
    commands[name] = unavailable(name)
end

for name, fn in pairs(commands) do
    mp.register_script_message(name, fn)
end
