-- Aligns mpv's hardware decoding AND render API with the configured
-- inference backend.
--
-- The native filter consumes the decoder's GPU frames directly, and the
-- frame type must match the backend selected in animejanai.conf:
--   TensorRT  -> CUDA frames  (hwdec=nvdec, Vulkan VO: mpv's only CUDA
--                render interop is CUDA<->Vulkan)
--   DirectML  -> D3D11 frames (hwdec=d3d11va, D3D11 VO: mpv has no
--                D3D11->Vulkan interop, so a Vulkan VO would hw-download
--                every output frame)
-- (backend=ncnn is retired and treated as DirectML by the shim.)
--
-- mpv.conf carries the TensorRT defaults (hwdec=nvdec,
-- gpu-api=vulkan,auto); this script overrides both for DirectML. Runs
-- at startup, before the first file loads, so the first play already
-- decodes and renders on the right path.

local mp = require 'mp'
local msg = require 'mp.msg'
local utils = require 'mp.utils'

local conf_path = mp.command_native({
    'expand-path', '~~/../animejanai/animejanai.conf'})

local function read_conf()
    local f = io.open(conf_path, 'r')
    if not f then
        return nil, false
    end
    local backend
    local rife = false
    local in_global = false
    for line in f:lines() do
        local sec = line:match('^%[(.-)%]')
        if sec then
            in_global = sec == 'global'
        elseif in_global then
            local v = line:match('^backend=([^%s]+)')
            if v then
                backend = v
            end
        end
        if line:match('^chain_%d+_rife=yes') or line:match('^chain_%d+_rife=true') then
            rife = true
        end
    end
    f:close()
    return backend, rife
end

local function exists(rel)
    local path = mp.command_native({'expand-path', '~~/../' .. rel})
    return utils.file_info(path) ~= nil
end

-- Component-pack sanity: a slim install (or one slimmed with
-- AnimeJaNaiUpdater --remove) may lack the pieces the conf asks for.
-- The filter would fail with a loader error; say what to run instead.
local function check_components(backend, rife_configured)
    local hints = {}
    if backend == 'tensorrt' then
        if not exists('animejanai/inference/nvinfer_11.dll') then
            hints[#hints + 1] =
                'TensorRT runtime not installed - press Ctrl+E to open ' ..
                'AnimeJaNai Manager (or run AnimeJaNaiUpdater.exe --auto)'
        else
            -- builder resources are only needed to build new engines; cached
            -- engines still run without them, so this is a soft warning
            local inf = mp.command_native({
                'expand-path', '~~/../animejanai/inference'})
            local files = utils.readdir(inf, 'files') or {}
            local has_builder = false
            for _, n in ipairs(files) do
                if n:match('^nvinfer_builder_resource_') then
                    has_builder = true
                    break
                end
            end
            if not has_builder then
                hints[#hints + 1] =
                    'No TensorRT kernel pack for this GPU - new engine builds ' ..
                    'will fail; press Ctrl+E to open AnimeJaNai Manager'
            end
        end
    end
    if rife_configured then
        local rdir = mp.command_native({'expand-path', '~~/../animejanai/rife'})
        local onnx = utils.readdir(rdir, 'files') or {}
        local has_model = false
        for _, n in ipairs(onnx) do
            if n:match('%.onnx$') then
                has_model = true
                break
            end
        end
        if not has_model then
            hints[#hints + 1] =
                'RIFE is enabled but the models are not installed - ' ..
                'press Ctrl+E to open AnimeJaNai Manager'
        end
    end
    if #hints == 0 then
        return
    end
    for _, h in ipairs(hints) do
        msg.warn(h)
    end
    -- Shown as an OSD overlay, not mp.osd_message: the player's
    -- now-playing message (filename) lands right after file-loaded and
    -- would overwrite the shared osd_message slot, hiding the hint -
    -- exactly when a first-run user needs it most. Overlays render on
    -- an independent channel. Wait for file-loaded anyway: before a VO
    -- exists there is nothing to render onto.
    local shown = false
    mp.register_event('file-loaded', function()
        if shown then
            return
        end
        shown = true
        local ov = mp.create_osd_overlay('ass-events')
        ov.data = '{\\an7\\fs28\\bord1.5\\1c&HFFFFFF&\\3c&H000000&}' ..
                  'AnimeJaNai: ' .. table.concat(hints, '\\N')
        ov:update()
        mp.add_timeout(20, function() ov:remove() end)
    end)
end

local backend_raw, rife_configured = read_conf()
local backend = (backend_raw or 'TensorRT'):lower()
local hwdec = 'nvdec'
if backend == 'directml' or backend == 'ncnn' then
    hwdec = 'd3d11va'
    mp.set_property('gpu-api', 'd3d11')
end
mp.set_property('hwdec', hwdec)
msg.info(string.format('backend %s -> hwdec=%s%s', backend, hwdec,
                       hwdec == 'd3d11va' and ', gpu-api=d3d11' or ''))
check_components(backend, rife_configured)
