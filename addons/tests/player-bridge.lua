-- Exercise the production bridge with an atomic file replacement/read failure.
-- luajit addons/tests/player-bridge.lua [path/to/animejanai_addons.lua]
local clock, tick, value, readable = 0, nil, nil, true
local filters, operations, warnings = {}, 0, 0
local mapping = 'Local\\AJN.PlayerFrames.' .. string.rep('a', 32)
package.preload['mp'] = function() return {
    get_property = function() return 'windows' end,
    get_property_number = function() return 10 end,
    get_time = function() return clock end,
    get_property_native = function() return filters end,
    command_native = function() return 'test-root' end,
    command_native_async = function() end,
    add_periodic_timer = function(_, callback) tick = callback end,
    register_script_message = function() end,
    observe_property = function() end,
    commandv = function(command, action, arg)
        if command ~= 'vf' then return end
        operations = operations + 1
        if action == 'remove' then
            for i = #filters, 1, -1 do
                if '@' .. filters[i].label == arg then table.remove(filters, i) end
            end
        else
            local label, name = arg:match('@([^:]+):ajn%-sample:name=%%%d+%%(.+)')
            assert(label and name)
            table.insert(filters, {label = label, name = 'ajn-sample', params = {name = name}})
        end
    end,
} end
package.preload['mp.utils'] = function() return {
    join_path = function(a, b) return a .. '/' .. b end,
    file_info = function() return {} end,
    readdir = function() return {'installed-addon'} end,
    parse_json = function() return value end,
} end
package.preload['mp.msg'] = function() return {
    warn = function() warnings = warnings + 1 end,
} end
local real_open = io.open
io.open = function()
    if readable then return {read = function() return '{}' end, close = function() end} end
end
dofile(arg[1] or 'BuildMpvUpscale2xAnimeJaNai/mpv-upscale-2x_animejanai/portable_config/scripts/animejanai_addons.lua')
assert(tick, 'installed addon should activate the bridge')
value = {schemaVersion = 1, instance = '10-0', revision = '1', available = true,
         enabled = false, analysisMapping = mapping}
tick(); clock = 1; tick()
assert(#filters == 1 and operations == 1)
readable = false; clock = 1.5; tick()
assert(#filters == 1 and operations == 1 and warnings == 0, 'brief failed read must keep the tap')
readable = true; value.revision = '2'; clock = 2; tick()
assert(#filters == 1 and operations == 1, 'recovery must not recreate the filter')
readable = false; clock = 5.01; tick()
assert(#filters == 0 and warnings == 1, 'failed reads must not renew the lease')
tick(); assert(warnings == 1, 'retirement should be logged once')
readable = true; value.revision = '3'; clock = 6; tick(); clock = 7; tick()
assert(#filters == 1, 'a fresh heartbeat should recover the tap')
value = nil; clock = 7.1; tick()
assert(#filters == 0 and warnings == 2, 'invalid content must retire immediately')
io.open = real_open
print('PASS transient read, heartbeat expiry, recovery and invalid control file')
