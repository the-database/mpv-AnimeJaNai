-- On startup, asks AnimeJaNaiUpdater.exe whether a newer mpv-upscale-2x_animejanai release exists.
-- If so, shows a one-time OSD prompt; Ctrl+U (or the AnimeJaNai > Install Update menu entry, both
-- mapped to the "animejanai-update" script-message) applies it: the updater is launched detached,
-- mpv quits so files unlock, the update is applied in place (user files preserved), and mpv relaunches.

local mp = require 'mp'
local msg = require 'mp.msg'
local utils = require 'mp.utils'

-- The updater ships at the install root, one level above portable_config (~~/ = config dir).
local exe_name = mp.get_property("platform") == "windows" and "AnimeJaNaiUpdater.exe" or "AnimeJaNaiUpdater"
local updater = mp.command_native({ "expand-path", "~~/../" .. exe_name })
local available_version = nil

-- Inside an AppImage the install tree is a read-only squashfs, so the in-place
-- updater can't apply. AppRun sets ANIMEJANAI_APPIMAGE (APPIMAGE is set by the
-- runtime too); in that case we point the user at the new AppImage instead.
local is_appimage = os.getenv("ANIMEJANAI_APPIMAGE") ~= nil or os.getenv("APPIMAGE") ~= nil
local releases_url = "github.com/the-database/mpv-upscale-2x_animejanai/releases"

local function start_update()
    if not available_version then
        mp.osd_message("AnimeJaNai is up to date.", 3)
        return
    end
    if is_appimage then
        mp.osd_message("AnimeJaNai " .. available_version ..
            " is available.\nDownload the new AppImage:\n" .. releases_url, 12)
        return
    end
    mp.osd_message("Installing AnimeJaNai " .. available_version .. " - mpv will close and reopen...", 5)
    -- Detached so it outlives mpv; the updater waits for mpv to exit, applies, then relaunches mpv.
    mp.command_native({
        name = "subprocess",
        args = { updater, "--apply" },
        detach = true,
        playback_only = false,
    })
    mp.add_timeout(1.0, function() mp.command("quit") end)
end

mp.register_script_message("animejanai-update", start_update)

-- The first start after the update that replaced mpv.net is still mpv.net: the old updater
-- relaunched the name it knew. Once --check has moved the install over (PLAYER_MIGRATED), move the
-- user to the new player: same file, same position (through watch-later), then close this one.
-- Only inside mpv.net: plain mpv has no "mpvnet" client, so the probe fails there, and under the
-- new player the interface is already loaded.
local function in_mpvnet()
    if mp.get_property_native("user-data/animejanai_ui/loaded") then
        return false
    end
    return mp.commandv("script-message-to", "mpvnet", "animejanai-probe") == true
end

local function hand_off_to_launcher()
    local root = updater:match("^(.*[/\\])") or ""
    local launcher = root .. "mpv-animejanai.exe"
    if mp.get_property("platform") ~= "windows" or not in_mpvnet() or not utils.file_info(launcher) then
        return
    end
    msg.info("mpv.net replaced: reopening in " .. launcher)
    mp.osd_message("AnimeJaNai now has its own player - reopening there...", 4)
    local args = { launcher }
    local path = mp.get_property("path")
    if path then
        mp.command("write-watch-later-config")
        args[#args + 1] = path
    end
    mp.command_native({ name = "subprocess", args = args, detach = true, playback_only = false })
    mp.add_timeout(0.5, function() mp.command("quit") end)
end

local function on_check(success, result)
    if not success or not result or result.status ~= 0 then
        return -- offline / updater missing / error: stay quiet
    end
    if (result.stdout or ""):find("PLAYER_MIGRATED", 1, true) then
        hand_off_to_launcher()
        return
    end
    local ver = (result.stdout or ""):match("UPDATE_AVAILABLE%s+(%S+)")
    if ver then
        available_version = ver
        -- The menu shows "Install Update" only while this is set (animejanai-ui/menu.conf).
        mp.set_property("user-data/animejanai/update-available", ver)
        msg.info("Update available: " .. ver)
        local how = is_appimage and "press Ctrl+U for the download link."
                                 or "press Ctrl+U to install."
        mp.osd_message("AnimeJaNai update " .. ver .. " available - " .. how, 8)
    else
        msg.verbose("AnimeJaNai is up to date.")
    end
end

mp.command_native_async({
    name = "subprocess",
    args = { updater, "--check" },
    capture_stdout = true,
    playback_only = false,
}, on_check)
