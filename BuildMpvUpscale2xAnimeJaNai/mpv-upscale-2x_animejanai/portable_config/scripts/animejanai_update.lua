-- On startup, asks AnimeJaNaiUpdater.exe whether a newer mpv-upscale-2x_animejanai release exists.
-- If so, shows a one-time OSD prompt; Ctrl+U (or the AnimeJaNai > Install Update menu entry, both
-- mapped to the "animejanai-update" script-message) applies it: the updater is launched detached,
-- mpv quits so files unlock, the update is applied in place (user files preserved), and mpv relaunches.

local mp = require 'mp'
local msg = require 'mp.msg'

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

local function on_check(success, result)
    if not success or not result or result.status ~= 0 then
        return -- offline / updater missing / error: stay quiet
    end
    local ver = (result.stdout or ""):match("UPDATE_AVAILABLE%s+(%S+)")
    if ver then
        available_version = ver
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
