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
--
-- Also applies the opt-in subtitle rendering mode: the Manager writes
-- [global] sub_render_mode=gpu into animejanai.conf, and this script applies
-- the [subs-gpu] profile from mpv-animejanai.conf. The settings live in that
-- profile (not here) so they can change with the player build without
-- touching this script or the Manager.

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
    local default_slot
    local sub_render_mode
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
            local d = line:match('^default_slot=(-?%d+)')
            if d then
                default_slot = tonumber(d)
            end
            local s = line:match('^sub_render_mode=([^%s]+)')
            if s then
                sub_render_mode = s
            end
        end
        if line:match('^chain_%d+_rife=yes') or line:match('^chain_%d+_rife=true') then
            rife = true
        end
    end
    f:close()
    return backend, rife, default_slot, sub_render_mode
end

local function exists(rel)
    -- the installed/writable tree (config-dir parent = install root)
    if utils.file_info(mp.command_native({'expand-path', '~~/../' .. rel})) ~= nil then
        return true
    end
    -- AppImage: the base runtime is bundled in the read-only payload, not under
    -- the writable data dir. $APPDIR (set by the AppImage runtime) is its root.
    local appdir = os.getenv('APPDIR')
    if appdir and utils.file_info(appdir .. '/' .. rel) ~= nil then
        return true
    end
    return false
end

-- Component-pack sanity: a slim install (or one slimmed with
-- AnimeJaNaiUpdater --remove) may lack the pieces the conf asks for.
-- The filter would fail with a loader error; say what to run instead.
local function check_components(backend, rife_configured)
    local hints = {}
    if backend == 'tensorrt' then
        -- TensorRT core library name differs per platform (Windows DLL vs
        -- Linux versioned .so).
        local nvinfer = mp.get_property('platform') == 'windows'
            and 'nvinfer_11.dll' or 'libnvinfer.so.11'
        if not exists('animejanai/inference/' .. nvinfer) then
            hints[#hints + 1] =
                'TensorRT runtime not installed - press Ctrl+E to open ' ..
                'AnimeJaNai Manager'
        else
            -- builder resources are only needed to build new engines; cached
            -- engines still run without them, so this is a soft warning
            local inf = mp.command_native({
                'expand-path', '~~/../animejanai/inference'})
            local files = utils.readdir(inf, 'files') or {}
            local has_builder = false
            for _, n in ipairs(files) do
                -- matches both nvinfer_builder_resource_* (Windows) and
                -- libnvinfer_builder_resource_* (Linux)
                if n:match('nvinfer_builder_resource_') then
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
    -- The player shows the filename through the same shared OSD text
    -- slot we'd use, right after file-loaded and for osd-duration ms
    -- (default 1s). Posting our hint immediately just loses that slot to
    -- the filename. So wait until the filename has cleared, then show
    -- our hint in the normal OSD position (where the filename was) for a
    -- good while. Reading osd-duration adapts if the user changed it; if
    -- the player used a longer title duration ours simply replaces it.
    local shown = false
    mp.register_event('file-loaded', function()
        if shown then
            return
        end
        shown = true
        local after = mp.get_property_number('osd-duration', 1000) / 1000 + 0.5
        mp.add_timeout(after, function()
            mp.osd_message('AnimeJaNai: ' .. table.concat(hints, '\n'), 10)
        end)
    end)
end

local backend_raw, rife_configured, default_slot, sub_render_mode = read_conf()
local backend = (backend_raw or 'TensorRT'):lower()
local hwdec = 'nvdec'
-- DirectML/ncnn use D3D11 frames (hwdec=d3d11va, gpu-api=d3d11). Windows-only:
-- there is no D3D11 on Linux, where only the TensorRT (CUDA/nvdec) backend
-- exists, so this branch is guarded behind the platform.
if (backend == 'directml' or backend == 'ncnn')
        and mp.get_property('platform') == 'windows' then
    hwdec = 'd3d11va'
    mp.set_property('gpu-api', 'd3d11')
end
mp.set_property('hwdec', hwdec)
msg.info(string.format('backend %s -> hwdec=%s%s', backend, hwdec,
                       hwdec == 'd3d11va' and ', gpu-api=d3d11' or ''))

-- Opt-in GPU subtitle rendering: only ever applied on top of the stable
-- defaults, never the reverse, so a user who set any of these options in
-- mpv.conf keeps them as long as the mode is off. Applied here rather than in
-- mpv.conf so the Manager stays the single writer of animejanai.conf.
--
-- A plain `apply-profile subs-gpu` would break mpv.conf's contract that your
-- own lines win: the config is fully parsed before scripts run, so the profile
-- would overwrite settings you made by hand. Instead each option is applied
-- only while it still holds the managed default from [animejanai] (or, for an
-- option that profile does not set, mpv's own default) - a different value can
-- only have come from your mpv.conf, so it is left alone. Both value sets are
-- read from profile-list, keeping the option lists in mpv-animejanai.conf.
--
-- Ambiguity worth knowing about: setting an option in mpv.conf to exactly the
-- managed default is indistinguishable from not setting it at all (mpv does
-- not record where a value came from), so that one does get the preset value.
local function profile_options(list, name)
    for _, p in ipairs(list) do
        if p.name == name then
            return p.options or {}
        end
    end
    return nil
end

local function apply_subs_gpu()
    local profiles = mp.get_property_native('profile-list') or {}
    local preset = profile_options(profiles, 'subs-gpu')
    if not preset then
        msg.warn('sub_render_mode=gpu but no subs-gpu profile - ' ..
                 'is mpv-animejanai.conf up to date?')
        return
    end
    local managed = profile_options(profiles, 'animejanai') or {}

    local function managed_value(key)
        for _, o in ipairs(managed) do
            if o.key == key then
                return o.value
            end
        end
        return mp.get_property('option-info/' .. key .. '/default-value')
    end

    local function norm(v)
        return (tostring(v or ''):match('^%s*(.-)%s*$')):lower()
    end

    for _, o in ipairs(preset) do
        if o.key == 'script-opts-append' then
            -- One "key=value" script option. It shares the script-opts map with
            -- everything else, so append it only when that key is unset.
            local k, v = tostring(o.value):match('^([^=]+)=(.*)$')
            local opts = mp.get_property_native('script-opts') or {}
            if k and opts[k] == nil then
                opts[k] = v
                mp.set_property_native('script-opts', opts)
            elseif k then
                msg.info('subs-gpu: keeping your script-opt ' .. k .. '=' .. tostring(opts[k]))
            end
        elseif norm(mp.get_property(o.key)) == norm(managed_value(o.key)) then
            mp.set_property(o.key, o.value)
        else
            msg.info(string.format('subs-gpu: keeping your %s=%s', o.key,
                                   tostring(mp.get_property(o.key))))
        end
    end
    msg.info('sub_render_mode gpu -> applied profile subs-gpu')
end

if (sub_render_mode or ''):lower() == 'gpu' then
    apply_subs_gpu()
end

-- The Manager's "Set as Default Profile" stores the chosen slot here. mpv
-- rebuilds the filter chain from the vf string (which bakes in Balanced, 1002)
-- on every file, so the active slot must be re-applied after each file loads or
-- every file after the first would snap back to Balanced. We re-apply the
-- *current* slot, not always default_slot: the default seeds the active slot at
-- startup, but once the user switches that choice sticks across file loads.
-- Restarting mpv re-reads default_slot and resets again.
local current_slot = default_slot
mp.register_script_message('aji-slot', function(slot)
    local n = tonumber(slot)
    if n then
        current_slot = n
    end
end)
mp.register_event('file-loaded', function()
    if current_slot then
        msg.info('applying slot ' .. current_slot)
        mp.commandv('script-message', 'aji-slot', tostring(current_slot))
    end
end)

check_components(backend, rife_configured)
