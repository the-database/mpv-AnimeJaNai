-- Slot switching without audio dropouts.
--
-- input.conf used to follow each `vf-command aji slot N` with an
-- unconditional `seek 0 exact` so the on-screen frame refreshes through
-- the newly selected slot. An exact seek flushes the audio pipeline
-- too, which is audible as a dropout on every switch. During playback
-- the new slot takes over within a few frames on its own (the filter
-- swaps chains without leaving the graph), so the refresh-seek is only
-- needed while paused - where audio is silent and the flush inaudible.
--
-- After a runtime failure, mpv's filter chain logs "Disabling filter aji
-- because it has failed." and plays on without the filter. Re-applying
-- the same vf (the keys' apply-profile) keeps that disabled instance:
-- the chain reuses a filter whose options are unchanged, and a seek
-- doesn't re-enable it. So the first slot key after a failure rebuilds
-- the filter with the chosen slot as its `slot=` option instead of
-- sending it a vf-command, which retries upscaling.

local mp = require 'mp'

local failed = false

mp.enable_messages("error")
mp.register_event("log-message", function(e)
    if e.prefix == "vf" and
       e.text:match("^Disabling filter aji because it has failed") then
        failed = true
    end
end)

-- A new file or a changed filter list builds a fresh filter.
local function clear()
    failed = false
end
mp.register_event("start-file", clear)
mp.observe_property("vf", "native", clear)

-- Removes @aji and adds it back with slot=<slot>, at the same position.
-- Two separate sets, so the chain can't match the new entry to the failed
-- instance. Returns false when there is no @aji to rebuild.
local function rebuild(slot)
    local list = mp.get_property_native("vf") or {}
    for i, f in ipairs(list) do
        if f.label == "aji" then
            local without = {}
            for j, g in ipairs(list) do
                if j ~= i then
                    without[#without + 1] = g
                end
            end
            f.params = f.params or {}
            f.params.slot = tostring(slot)
            mp.msg.info("Rebuilding the failed upscaling filter for slot " .. slot)
            failed = false
            mp.set_property_native("vf", without)
            mp.set_property_native("vf", list)
            return true
        end
    end
    return false
end

mp.register_script_message('aji-slot', function(slot)
    if not (failed and rebuild(slot)) then
        mp.commandv('vf-command', 'aji', 'slot', slot)
    end
    if mp.get_property_native('pause') then
        mp.command('no-osd seek 0 exact')
    end
end)
