-- Slot switching without audio dropouts.
--
-- input.conf used to follow each `vf-command aji slot N` with an
-- unconditional `seek 0 exact` so the on-screen frame refreshes through
-- the newly selected slot. An exact seek flushes the audio pipeline
-- too, which is audible as a dropout on every switch. During playback
-- the new slot takes over within a few frames on its own (the filter
-- swaps chains without leaving the graph), so the refresh-seek is only
-- needed while paused - where audio is silent and the flush inaudible.

local mp = require 'mp'

mp.register_script_message('aji-slot', function(slot)
    mp.commandv('vf-command', 'aji', 'slot', slot)
    if mp.get_property_native('pause') then
        mp.command('no-osd seek 0 exact')
    end
end)
