
msg = require 'mp.msg'

local timer
local timepos
local fileloaded = false

local savedvf

function file_loaded()
    fileloaded = true
end

function on_seeking_change(name, value)
    if fileloaded then
        fileloaded = false
        return
    end
    if (timepos == mp.get_property("time-pos")) then
        msg.info("blocked")
        timepos = mp.get_property("time-pos")
        return
    else
        timepos = mp.get_property("time-pos")
    end
    msg.info("on_seeking_change", value)
    if value == true then
        savedvf = mp.get_property("vf")
        mp.command("apply-profile upscale-off")
        msg.info("upscale-off for seeking")
    else
        if timer then
            msg.info("kill timer")
            timer:kill()
        end
        timer = mp.add_timeout(1, function()
            if savedvf ~= nil then
                mp.command("set vf ".. savedvf)
                msg.info("upscale-on after finished seeking")
            end
        end)
    end
end

mp.register_event("file-loaded", file_loaded)
mp.observe_property("seeking", "bool", on_seeking_change)

