local open = io.open

local showingMessage = false
local MAX_DURATION = 2147483

-- Why upscaling stopped during playback, or nil. When the filter fails (an
-- inference error mid-playback, a chain that won't configure), mpv's filter
-- chain logs "Disabling filter aji because it has failed." and plays the rest
-- of the file without it. Nothing else reports that, and the stats log still
-- describes the last chain, so this watches the log for that line and keeps
-- the filter's last error as the reason. The failed filter stays off until it
-- is rebuilt: the next file, a profile key (animejanai_slot.lua rebuilds it),
-- or upscaling turned off and on.
local stopped = nil
local last_error = nil

local function short(text)
    text = (text or ""):match("^[^\r\n]*"):gsub("%s+$", "")
    if #text > 120 then
        text = text:sub(1, 117) .. "..."
    end
    return text
end

local function stopped_message()
    return "Upscaling stopped: " .. stopped ..
           "\nPress a profile key (Shift+1-3, Ctrl+1-9) or open the file again to retry." ..
           "\nPress ~ for the console log."
end

mp.enable_messages("error")
mp.register_event("log-message", function(e)
    if e.prefix == "animejanai" then
        last_error = e.text
    elseif e.prefix == "vf" then
        local name = e.text:match("^Disabling filter (%S+) because it has failed")
        if name == "aji" or name == "animejanai" then
            stopped = last_error and short(last_error) or "the upscaling filter failed"
            mp.msg.error("Upscaling stopped: " .. stopped)
            if showingMessage then
                mp.osd_message(stopped_message(), MAX_DURATION)
            else
                mp.osd_message("AnimeJaNai: " .. stopped_message(), 10)
            end
        end
    end
end)

-- A new file or a changed filter list builds a fresh filter.
local function clear()
    stopped = nil
    last_error = nil
end
mp.register_event("start-file", clear)
mp.observe_property("vf", "native", clear)

-- Held-frame reuse, from the filter's stats line "held frames: reused <r> of
-- <n>" (a share such as "(30.9%)" may follow): of the <n> frames it decided,
-- <r> got the previous frame's upscale again instead of running the model.
-- The line becomes "Reused: 31% of frames". The filter only writes it while
-- reuse applies, so without it (reuse turned off, RIFE or a temporal model,
-- an older filter) nothing is shown; a line that doesn't add up is dropped.
local function reuse_percent(reused, frames)
    local pct = math.floor(100 * reused / frames + 0.5)
    if pct == 0 and reused > 0 then
        return "<1%"
    end
    return pct .. "%"
end

local function format_stats(content)
    if content == nil then
        return nil
    end
    local out = {}
    for line, eol in content:gmatch("([^\r\n]*)(\r?\n?)") do
        local reused, frames = line:match("held frames:%s*reused%s+(%d+)%s+of%s+(%d+)")
        if reused then
            reused, frames = tonumber(reused), tonumber(frames)
            if frames > 0 and reused <= frames then
                out[#out + 1] = "Reused: " .. reuse_percent(reused, frames) .. " of frames" .. eol
            end
        else
            out[#out + 1] = line .. eol
        end
    end
    return table.concat(out)
end

-- Function to be called when the custom action is triggered
function show_animejanai_stats()
    if showingMessage then
        mp.osd_message("")  -- Clear the OSD message
        showingMessage = false
    else
        local message = ""

        if mp.get_property("vf") == "" then
            message = "Upscaling is disabled"
        elseif stopped then
            message = stopped_message()
        else
            local data_file_path = (mp.command_native({'expand-path', "~~/../animejanai/currentanimejanai.log"}))
            message = format_stats(read_file(data_file_path))

            if message == "" then
                message = "Error during upscale; press ~ to view error in console"
            end
        end

        mp.osd_message(message, MAX_DURATION)
        showingMessage = true
    end
end

function read_file(path)
    local file = open(path, "r") -- r read mode and b binary mode
    if not file then return nil end
    local content = file:read "*a" -- *a or *all reads the whole file
    file:close()
    return content
end


-- Register the custom action to a key binding (e.g., "Ctrl+Shift+C")
mp.add_key_binding("Ctrl+j", "show_animejanai_stats", show_animejanai_stats)