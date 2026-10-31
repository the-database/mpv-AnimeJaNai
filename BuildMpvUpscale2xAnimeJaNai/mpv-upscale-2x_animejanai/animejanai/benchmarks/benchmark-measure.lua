-- Control only. FPS comes from mpv's native `end video-flip` trace events,
-- not Lua polling, timeline position, source FPS, or an estimated frame number.
local mp = require 'mp'
local utils = require 'mp.utils'
local config_file = assert(io.open(assert(os.getenv('AJ_BENCH_CONFIG')), 'rb'))
local cfg = assert(utils.parse_json(config_file:read('*a')))
config_file:close()
local armed, measuring, finished = false, false, false
local started, drops, decoder_drops, viewport

local function finish(error_message)
    if finished then return end
    finished = true
    local ended = mp.get_time()
    local report = {
        status = error_message and 'failed' or 'ok', error = error_message,
        start_seconds = started, end_seconds = ended,
        input = mp.get_property_native('video-params'),
        output = mp.get_property_native('video-out-params'),
        viewport = mp.get_property_native('osd-dimensions'),
        source_fps = mp.get_property_number('container-fps'),
        display_hz = mp.get_property_number('display-fps'),
        vo = mp.get_property('current-vo'),
        hwdec = mp.get_property('hwdec-current'),
        dropped_frames = mp.get_property_number('frame-drop-count', 0) - (drops or 0),
        decoder_dropped_frames = mp.get_property_number('decoder-frame-drop-count', 0) - (decoder_drops or 0),
        mpv_version = mp.get_property('mpv-version'),
        options = {},
    }
    for _, name in ipairs({'gpu-api', 'gpu-context', 'deband', 'glsl-shaders',
            'video-sync', 'untimed', 'vulkan-swap-mode', 'd3d11-sync-interval',
            'opengl-swapinterval', 'interpolation', 'scale', 'cscale', 'dscale'}) do
        report.options[name] = mp.get_property_native('options/' .. name)
    end
    local file = assert(io.open(cfg.report, 'wb'))
    file:write(utils.format_json(report))
    file:close()
    mp.commandv('quit', error_message and '2' or '0')
end

local function valid_output()
    local input = mp.get_property_native('video-params') or {}
    local output = mp.get_property_native('video-out-params') or {}
    return input.w == cfg.width and input.h == cfg.height and
        output.w == cfg.width * 2 and output.h == cfg.height * 2
end

mp.observe_property('video-out-params', 'native', function()
    if finished then return end
    if measuring and not valid_output() then
        finish('Output changed during measurement; expected the selected 2x model.')
    elseif not armed and valid_output() then
        armed = true
        mp.add_timeout(cfg.warmup_seconds, function()
            if finished then return end
            if not valid_output() then return finish('Upscale was not ready after warmup.') end
            if mp.get_property_native('pause') then return finish('Player is paused.') end
            drops = mp.get_property_number('frame-drop-count', 0)
            decoder_drops = mp.get_property_number('decoder-frame-drop-count', 0)
            viewport = mp.get_property_native('osd-dimensions') or {}
            started = mp.get_time()
            measuring = true
            mp.add_timeout(cfg.seconds, function() finish(nil) end)
        end)
    end
end)
mp.observe_property('pause', 'bool', function(_, paused)
    if measuring and paused then finish('Playback paused during measurement.') end
end)
mp.observe_property('osd-dimensions', 'native', function(_, dimensions)
    if measuring and dimensions and mp.get_property('current-vo') ~= 'null' and
            (dimensions.w ~= viewport.w or dimensions.h ~= viewport.h) then
        finish('Renderer size changed during measurement.')
    end
end)
mp.observe_property('window-minimized', 'bool', function(_, minimized)
    if measuring and minimized and mp.get_property('current-vo') ~= 'null' then
        finish('Renderer was minimized during measurement.')
    end
end)
mp.register_event('end-file', function(event)
    -- mpv.net opens a folder playlist before loading its first video.
    if event.reason == 'redirect' and not armed then return end
    if not finished then finish('Video ended before measurement completed: ' .. tostring(event.reason)) end
end)
mp.add_timeout(cfg.warmup_seconds + cfg.seconds + 90, function()
    finish('Timed out waiting for the selected 2x upscaler.')
end)
