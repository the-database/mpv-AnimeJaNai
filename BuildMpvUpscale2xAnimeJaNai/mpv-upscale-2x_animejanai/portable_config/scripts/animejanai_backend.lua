-- Aligns mpv's hardware decoding with the configured inference backend.
--
-- The native filter consumes the decoder's GPU frames directly, and the
-- frame type must match the backend selected in animejanai.conf:
--   TensorRT  -> CUDA frames  (hwdec=nvdec)
--   DirectML  -> D3D11 frames (hwdec=d3d11va)
-- (backend=ncnn is retired and treated as DirectML by the shim.)
--
-- Runs at startup, before the first file loads, so the first play
-- already decodes on the right path.

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
end
mp.set_property('hwdec', hwdec)
msg.info(string.format('backend %s -> hwdec=%s', backend, hwdec))
