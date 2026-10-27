-- Ctrl+E: launch AnimeJaNai Manager (the "animejanai-manager" script-message).
-- input.conf can't `run "~~/../AnimeJaNaiManager"` directly: mpv's run command
-- passes its arguments through as-is, without expanding ~~/, so the path only
-- resolves here. The Manager ships at the install root, one level above
-- portable_config (~~/ = config dir), with no extension on Linux.

local mp = require 'mp'

local exe_name = mp.get_property("platform") == "windows" and "AnimeJaNaiManager.exe" or "AnimeJaNaiManager"
local manager = mp.command_native({ "expand-path", "~~/../" .. exe_name })

mp.register_script_message('animejanai-manager', function()
    mp.osd_message("Launching AnimeJaNai Manager...")
    -- Detached so closing mpv doesn't close the Manager.
    mp.command_native_async({
        name = "subprocess",
        args = { manager },
        detach = true,
        playback_only = false,
    }, function() end)
end)
