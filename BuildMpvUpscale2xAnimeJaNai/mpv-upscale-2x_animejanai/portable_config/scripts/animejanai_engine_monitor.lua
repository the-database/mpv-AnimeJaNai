-- Engine-build monitor for vf_animejanai.
--
-- TensorRT engines are built on first play (per model and resolution).
-- Builds run in the background, but TensorRT selects kernels by timing
-- them on the GPU and recommends an idle GPU for the best result - so by
-- default this script pauses playback while a build runs, narrates what
-- is happening on the OSD, and resumes automatically when the engine is
-- ready. Unpausing manually during a build is respected: the video keeps
-- playing (unupscaled) and the script stays hands-off.
--
-- The filter rewrites the stats log on every (re)configure; a
-- "Building TensorRT engine" line means a build is in flight.
--
-- script-opts (prefix animejanai_engine_monitor-):
--   auto_pause=yes|no   pause playback during builds (default yes)
--   stats_path=...      override the stats log location

local mp = require 'mp'
local msg = require 'mp.msg'
local options = require 'mp.options'

local o = {
    auto_pause = true,
    poll_interval = 0.25,
    stats_path = "~~/../animejanai/currentanimejanai.log",
}
options.read_options(o, "animejanai_engine_monitor")

local stats_path = mp.command_native({'expand-path', o.stats_path})

local building = false
local we_paused = false
local started_at = 0
local build_name = "?"
local build_res = "?"

local function read_stats()
    local f = io.open(stats_path, "r")
    if not f then return nil end
    local s = f:read("*a")
    f:close()
    return s
end

mp.add_periodic_timer(o.poll_interval, function()
    local s = read_stats()
    local b = s ~= nil and s:find("Building TensorRT engine", 1, true) ~= nil

    -- the user taking over wins: if they unpause mid-build, stay hands-off
    if we_paused and not mp.get_property_bool("pause") then
        we_paused = false
    end

    if b and not building then
        building = true
        started_at = mp.get_time()
        if o.auto_pause and not mp.get_property_bool("pause") then
            mp.set_property_bool("pause", true)
            we_paused = true
            msg.info("engine build started; pausing playback")
        else
            msg.info("engine build started")
        end
    elseif not b and building then
        building = false
        local failed = s ~= nil and s:find("build FAILED", 1, true) ~= nil
        msg.info(string.format("engine build finished after %ds%s",
            math.floor(mp.get_time() - started_at),
            we_paused and "; resuming playback" or ""))
        if we_paused then
            mp.set_property_bool("pause", false)
            we_paused = false
        end
        if failed then
            local log_path = s and s:match("%(see ([^%)]+)%)") or
                             "the .build.log file next to the model"
            mp.osd_message(string.format(
                "AnimeJaNai: Building TensorRT engine for %s for %s " ..
                "failed. Upscaling is disabled. (details: %s).",
                build_name, build_res, log_path), 10)
        else
            mp.osd_message(string.format(
                "AnimeJaNai: Building TensorRT engine for %s for %s " ..
                "completed successfully. Upscaling is active.",
                build_name, build_res), 5)
        end
    end

    if building then
        local n, r = s:match(
            "Building TensorRT engine for ([^%s]+) for ([^%s]+)")
        if n then
            build_name = n
            build_res = r
        end
        local elapsed = math.floor(mp.get_time() - started_at)
        local second = we_paused and "Playback will resume on completion."
                                  or "Upscaling will activate on completion."
        mp.osd_message(string.format(
            "AnimeJaNai: Building TensorRT engine for %s for %s " ..
            "(%ds, usually about a minute)\n%s",
            build_name, build_res, elapsed, second),
            o.poll_interval + 0.5)
        -- while paused no frames flow, so the filter would never notice the
        -- finished build; this no-op command wakes it so it polls
        mp.commandv("vf-command", "aji", "poll", "1")
    end
end)
