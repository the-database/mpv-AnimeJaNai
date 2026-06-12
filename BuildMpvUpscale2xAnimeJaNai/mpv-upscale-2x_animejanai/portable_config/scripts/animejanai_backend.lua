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

local conf_path = mp.command_native({
    'expand-path', '~~/../animejanai/animejanai.conf'})

local function read_backend()
    local f = io.open(conf_path, 'r')
    if not f then
        return nil
    end
    local backend
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
    end
    f:close()
    return backend
end

local backend = (read_backend() or 'TensorRT'):lower()
local hwdec = 'nvdec'
if backend == 'directml' or backend == 'ncnn' then
    hwdec = 'd3d11va'
    mp.set_property('gpu-api', 'd3d11')
end
mp.set_property('hwdec', hwdec)
msg.info(string.format('backend %s -> hwdec=%s%s', backend, hwdec,
                       hwdec == 'd3d11va' and ', gpu-api=d3d11' or ''))
