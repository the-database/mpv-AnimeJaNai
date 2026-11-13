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
    local display_rate_match = false
    local rife = false
    local gpus = {}
    local in_global = false
    for line in f:lines() do
        line = line:gsub('\r$', '')
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
            local r = line:match('^display_rate_match=([^%s]+)')
            if r then
                display_rate_match = r:lower() == 'yes' or r:lower() == 'true'
            end
            -- the Manager's GPU choice per backend; absent or empty = Auto
            local gb, gv = line:match('^gpu_(%a+)=%s*(.-)%s*$')
            if gb and gv ~= '' then
                gpus[gb:lower()] = gv
            end
        end
        if line:match('^chain_%d+_rife=yes') or line:match('^chain_%d+_rife=true') then
            rife = true
        end
    end
    f:close()
    return backend, rife, default_slot, sub_render_mode, display_rate_match, gpus
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

-- The pack store's aji-runtime.json, which the updater has aji-runtime write
-- beside libaji: the folders libaji loads TensorRT and its builder kernels
-- from (absolute, or relative to the file when the store is inside this
-- install: a portable copy that can be moved). nil without it (an install from
-- before the store, or the AppImage): then they are in the inference folder.
local function runtime_packs()
    local inf = mp.command_native({'expand-path', '~~/../animejanai/inference'})
    local f = io.open(utils.join_path(inf, 'aji-runtime.json'), 'rb')
    if not f then
        return nil
    end
    local text = f:read('*a')
    f:close()
    local t = utils.parse_json(text)
    if type(t) ~= 'table' then
        return nil
    end
    -- relative paths are relative to the file, as libaji reads them
    local function dir(p)
        return type(p) == 'string' and p ~= '' and utils.join_path(inf, p) or nil
    end
    local builders = {}
    if type(t.trt_builder) == 'table' then
        for _, p in ipairs(t.trt_builder) do
            builders[#builders + 1] = dir(p)
        end
    end
    return { trt = dir(t.trt), trt_builder = builders }
end

-- Whether the TensorRT runtime is installed: the store's TensorRT pack when
-- aji-runtime.json names one, else the inference folder's.
local function trt_runtime_installed()
    -- the core library's name differs per platform (Windows DLL vs Linux
    -- versioned .so)
    local nvinfer = mp.get_property('platform') == 'windows'
        and 'nvinfer_11.dll' or 'libnvinfer.so.11'
    local packs = runtime_packs()
    if packs and packs.trt then
        return utils.file_info(utils.join_path(packs.trt, nvinfer)) ~= nil
    end
    return exists('animejanai/inference/' .. nvinfer)
end

-- The pack store's folder when it is missing (a drive that isn't connected):
-- the per-user store moved to another drive, or a folder chosen in AnimeJaNai
-- Manager > Components > Storage. Then TensorRT only looks uninstalled, its
-- packs are on that drive. From `aji-runtime location --json` (aji-runtime
-- 0.2.0+, at the install root, or in the AppImage), asked only when
-- aji-runtime.json names a TensorRT pack that isn't there. nil otherwise;
-- checked once.
local store_missing_checked, store_missing_folder
local function trt_store_missing()
    if store_missing_checked then
        return store_missing_folder
    end
    store_missing_checked = true
    local packs = runtime_packs()
    if not (packs and packs.trt) or trt_runtime_installed() then
        return nil
    end
    local name = mp.get_property('platform') == 'windows'
        and 'aji-runtime.exe' or 'aji-runtime'
    local exe = mp.command_native({'expand-path', '~~/../' .. name})
    if utils.file_info(exe) == nil then
        local appdir = os.getenv('APPDIR')
        exe = appdir and utils.join_path(appdir, name)
        if not exe or utils.file_info(exe) == nil then
            return nil
        end
    end
    local r = mp.command_native({
        name = 'subprocess',
        args = {exe, 'location', '--json'},
        capture_stdout = true,
        capture_stderr = true,
        playback_only = false,
    })
    if not r or r.status ~= 0 then
        return nil
    end
    local t = utils.parse_json(r.stdout or '')
    if type(t) == 'table' and t.available == false then
        -- a moved per-user store's files are in its target; any other store's
        -- in its root
        local folder = t.moved == true and t.target or t.root
        store_missing_folder = type(folder) == 'string' and folder or '?'
    end
    return store_missing_folder
end

local function store_missing_hint(folder)
    local fix = os.getenv('ANIMEJANAI_APPIMAGE') == '1'
        and 'reset the location in the JaNai app you moved it with'
        or 'choose another place in AnimeJaNai Manager (Ctrl+E) > Components > Storage'
    return 'The TensorRT runtime\'s folder ' .. folder .. ' isn\'t available. ' ..
        'Is its drive connected? Reconnect it, or ' .. fix
end

-- Whether a TensorRT builder (kernel) pack is installed: in the store when
-- aji-runtime.json names the TensorRT pack, else in the inference folder.
local function trt_builder_installed()
    local packs = runtime_packs()
    if packs and packs.trt then
        for _, d in ipairs(packs.trt_builder) do
            if utils.file_info(d) ~= nil then
                return true
            end
        end
        return false
    end
    local inf = mp.command_native({
        'expand-path', '~~/../animejanai/inference'})
    local files = utils.readdir(inf, 'files') or {}
    for _, n in ipairs(files) do
        -- matches both nvinfer_builder_resource_* (Windows) and
        -- libnvinfer_builder_resource_* (Linux)
        if n:match('nvinfer_builder_resource_') then
            return true
        end
    end
    return false
end

-- Component-pack sanity: a slim install (or one slimmed with
-- AnimeJaNaiUpdater --remove) may lack the pieces the conf asks for.
-- The filter would fail with a loader error; say what to run instead.
local function check_components(backend, rife_configured, fallback_hint)
    local hints = {}
    if fallback_hint then
        hints[#hints + 1] = fallback_hint
    end
    if backend == 'tensorrt' then
        if not trt_runtime_installed() then
            local missing = trt_store_missing()
            hints[#hints + 1] = missing and store_missing_hint(missing) or
                'TensorRT runtime not installed - press Ctrl+E to open ' ..
                'AnimeJaNai Manager'
        else
            -- builder resources are only needed to build new engines; cached
            -- engines still run without them, so this is a soft warning
            if not trt_builder_installed() then
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

-- TensorRT needs an NVIDIA GPU with compute capability 7.5+ (GeForce RTX 20 /
-- GTX 16 series and newer): the shipped TensorRT 11 on CUDA 13 supports nothing
-- older, and aji's CUDA kernels start at sm75. The trt-ptx pack can't help an
-- older GPU (PTX only JIT-compiles forward), so such a GPU counts as no NVIDIA
-- GPU for TensorRT: DirectML on Windows. The updater (GpuSupport.cs,
-- TrtMinComputeCapability) and installer/linux/AppRun apply the same rule.
local TRT_MIN_COMPUTE = { 7, 5 }

local function trt_capable(g)
    return g.major > TRT_MIN_COMPUTE[1] or
        (g.major == TRT_MIN_COMPUTE[1] and g.minor >= TRT_MIN_COMPUTE[2])
end

-- Parses `nvidia-smi --query-gpu=uuid,compute_cap,name --format=csv,noheader`:
-- { {uuid, name, major, minor}, ... }, the uuid as the Manager writes
-- gpu_tensorrt (no "GPU-" prefix, lowercase). nil when a line can't be read
-- (a driver too old to report compute_cap): then nothing is decided from it.
local function parse_nvidia_gpus(text)
    local list = {}
    for line in (text or ''):gmatch('[^\r\n]+') do
        if line:match('%S') then
            local uuid, major, minor, name =
                line:match('^%s*([^,]-)%s*,%s*(%d+)%.(%d+)%s*,%s*(.-)%s*$')
            if not uuid then
                return nil
            end
            list[#list + 1] = {
                uuid = uuid:gsub('^[Gg][Pp][Uu]%-', ''):lower(),
                name = name,
                major = tonumber(major),
                minor = tonumber(minor),
            }
        end
    end
    return #list > 0 and list or nil
end

-- This PC's NVIDIA GPUs (parse_nvidia_gpus), from nvidia-smi, which the driver
-- installs; nil without it. Asked once, only while the backend is TensorRT.
-- ANIMEJANAI_NVIDIA_GPUS stands in for nvidia-smi's output (testing).
local nvidia_gpus_checked, nvidia_gpus_list
local function nvidia_gpus()
    if nvidia_gpus_checked then
        return nvidia_gpus_list
    end
    nvidia_gpus_checked = true
    local fake = os.getenv('ANIMEJANAI_NVIDIA_GPUS')
    if fake then
        nvidia_gpus_list = parse_nvidia_gpus(fake:gsub(';', '\n'))
        return nvidia_gpus_list
    end
    local exe = 'nvidia-smi'
    if mp.get_property('platform') == 'windows' then
        local sysroot = os.getenv('SystemRoot') or 'C:\\Windows'
        exe = sysroot .. '\\System32\\nvidia-smi.exe'
        if utils.file_info(exe) == nil then
            -- drivers before the DCH layout
            local pf = os.getenv('ProgramFiles') or 'C:\\Program Files'
            exe = pf .. '\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe'
            if utils.file_info(exe) == nil then
                return nil
            end
        end
    end
    local r = mp.command_native({
        name = 'subprocess',
        args = {exe, '--query-gpu=uuid,compute_cap,name', '--format=csv,noheader'},
        capture_stdout = true,
        capture_stderr = true,
        playback_only = false,
    })
    if r and r.status == 0 then
        nvidia_gpus_list = parse_nvidia_gpus(r.stdout)
    end
    return nvidia_gpus_list
end

-- The NVIDIA GPUs TensorRT can use and the ones too old for it, or nil when
-- that isn't known.
local function trt_gpus()
    local all = nvidia_gpus()
    if not all then
        return nil
    end
    local ok, old = {}, {}
    for _, g in ipairs(all) do
        if trt_capable(g) then
            ok[#ok + 1] = g
        else
            old[#old + 1] = g
        end
    end
    return ok, old
end

local function too_old_message(old)
    local names = {}
    for _, g in ipairs(old) do
        names[#names + 1] = string.format('%s is compute %d.%d', g.name, g.major, g.minor)
    end
    return string.format('TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series ' ..
        'or newer GPU (compute capability %d.%d+); %s',
        TRT_MIN_COMPUTE[1], TRT_MIN_COMPUTE[2], table.concat(names, ', '))
end

-- Whether TensorRT can run on this Windows PC: it needs NVIDIA's driver
-- (nvcuda.dll is installed with it), an NVIDIA GPU new enough for it, and the
-- TensorRT runtime component. The second value says why not; the third is the
-- GPUs too old for TensorRT when that is the reason.
local function trt_usable()
    local sysroot = os.getenv('SystemRoot') or 'C:\\Windows'
    if utils.file_info(sysroot .. '\\System32\\nvcuda.dll') == nil then
        return false, 'no NVIDIA GPU driver'
    end
    local ok, old = trt_gpus()
    if ok and #ok == 0 and #old > 0 then
        return false, 'NVIDIA GPU too old for TensorRT', old
    end
    if not trt_runtime_installed() then
        return false, 'TensorRT runtime not installed'
    end
    return true
end

-- Rewrites [global] backend= to DirectML and marks it as an automatic
-- fallback, the same two keys the Manager writes when it makes this switch.
-- With backend_auto_fallback=yes the Manager switches back to TensorRT by
-- itself once TensorRT can run. Returns false if the conf can't be updated.
local function switch_conf_to_directml()
    local f = io.open(conf_path, 'rb')
    if not f then
        return false
    end
    local s = f:read('*a')
    f:close()
    local nl = s:find('\r\n', 1, true) and '\r\n' or '\n'
    local trailing = s:sub(-1) == '\n'
    if not trailing then
        s = s .. '\n'
    end
    local out = {}
    local in_global, seen_global = false, false
    local have_backend, have_fallback = false, false
    local function close_global()
        if not have_backend then
            out[#out + 1] = 'backend=DirectML'
        end
        if not have_fallback then
            out[#out + 1] = 'backend_auto_fallback=yes'
        end
    end
    for line in s:gmatch('([^\n]*)\n') do
        line = line:gsub('\r$', '')
        local sec = line:match('^%[(.-)%]')
        if sec then
            if in_global then
                close_global()
            end
            in_global = sec == 'global'
            seen_global = seen_global or in_global
        elseif in_global and line:match('^backend=') then
            line = 'backend=DirectML'
            have_backend = true
        elseif in_global and line:match('^backend_auto_fallback=') then
            line = 'backend_auto_fallback=yes'
            have_fallback = true
        end
        out[#out + 1] = line
    end
    if in_global then
        close_global()
    end
    if not seen_global then
        return false
    end
    f = io.open(conf_path, 'wb')
    if not f then
        return false
    end
    f:write(table.concat(out, nl) .. (trailing and nl or ''))
    f:close()
    return true
end

local backend_raw, rife_configured, default_slot, sub_render_mode, display_rate_match, gpu_choice = read_conf()
gpu_choice = gpu_choice or {}
if display_rate_match and mp.get_property('platform') == 'windows' then
    local ok, err = mp.set_property('display-rate-match', 'yes')
    if not ok then
        msg.error('Automatic Display Refresh Rate requires a matching AJN player build: ' .. tostring(err))
    end
end
local backend = (backend_raw or 'TensorRT'):lower()

-- A fresh install ships backend=TensorRT, and until now only the Manager
-- switched it to DirectML on a PC where TensorRT can't run, so AMD and Intel
-- users got no upscaling until they opened it. Make that switch here, at
-- startup, before the filter reads the conf.
local fallback_hint
-- A GPU too old for TensorRT where nothing else can upscale (Linux has no
-- DirectML, or the conf couldn't be switched): the filter is taken out at
-- startup with this message, rather than failing at the engine build.
local trt_unavailable
if backend == 'tensorrt' and mp.get_property('platform') == 'windows' then
    local ok, why, old = trt_usable()
    -- TensorRT on a drive that isn't connected is still installed: no switch
    -- (check_components says what to do)
    local away = why == 'TensorRT runtime not installed' and trt_store_missing()
    if not ok and not away and switch_conf_to_directml() then
        msg.warn('TensorRT can\'t run here (' .. why .. '); switched animejanai.conf to DirectML')
        backend = 'directml'
        if why == 'TensorRT runtime not installed' then
            -- an NVIDIA GPU: TensorRT is faster, so say how to get it
            fallback_hint = 'Using DirectML: the TensorRT runtime is not installed - ' ..
                'press Ctrl+E to open AnimeJaNai Manager'
        elseif old then
            fallback_hint = 'Using DirectML: ' .. too_old_message(old)
        end
    elseif old then
        trt_unavailable = too_old_message(old) .. '. Select DirectML in AnimeJaNai Manager (Ctrl+E)'
    end
elseif backend == 'tensorrt' then
    -- Linux: TensorRT is the only backend, so a GPU too old for it can't upscale
    local ok, old = trt_gpus()
    if ok and #ok == 0 and #old > 0 then
        trt_unavailable = too_old_message(old) .. ', the only upscaling engine on Linux'
    end
end
if trt_unavailable then
    msg.error(trt_unavailable)
    mp.commandv('vf', 'remove', '@aji')
    -- shown by check_components, without its TensorRT install hints
    fallback_hint = 'Upscaling is off: ' .. trt_unavailable
end

local hwdec = 'nvdec'
-- DirectML/ncnn use D3D11 frames (hwdec=d3d11va, gpu-api=d3d11). Windows-only:
-- there is no D3D11 on Linux, where only the TensorRT (CUDA/nvdec) backend
-- exists, so this branch is guarded behind the platform.
if (backend == 'directml' or backend == 'ncnn')
        and mp.get_property('platform') == 'windows' then
    hwdec = 'd3d11va'
    mp.set_property('gpu-api', 'd3d11')
end

-- Which GPU upscales. The Manager writes [global] gpu_tensorrt (a Vulkan device
-- UUID: CUDA decoding, and so the filter, follows the Vulkan device) and
-- gpu_directml (a D3D11 adapter's PCI slot, pci:<bus>:<device>.<function>, which
-- the fork's --d3d11-adapter matches exactly; or an adapter name when Windows
-- reports no slot, which mpv matches as a prefix of the adapter's name).
-- Each is applied only while its mpv option is still unset, so a device chosen
-- in mpv.conf wins.
local function unset(opt)
    return (mp.get_property(opt) or '') == ''
end
-- mpv rejects a Vulkan device that isn't there (e.g. a removed GPU) and keeps
-- its own pick; an unknown D3D11 adapter name falls back to the default adapter
local function apply_gpu(opt, value)
    if mp.set_property(opt, value) then
        msg.info('GPU choice: ' .. opt .. '=' .. value)
    else
        msg.warn('GPU choice ' .. value .. ' not found; using the default GPU')
    end
end
if hwdec == 'nvdec' then
    -- Next to a GPU too old for TensorRT, TensorRT runs on one it supports: the
    -- chosen GPU unless it is the old one (a choice from before the Manager left
    -- such GPUs out), else the first. Without an old GPU, Auto stays mpv's pick.
    local ok, old = trt_gpus()
    local choice = gpu_choice.tensorrt and gpu_choice.tensorrt:lower()
    local new_enough = ok and #ok > 0 and #old > 0 and ok[1]
    if new_enough and choice then
        for _, g in ipairs(old) do
            if g.uuid == choice then
                -- check_components logs it and shows it on the OSD
                fallback_hint = too_old_message({g}) .. '; upscaling on ' .. new_enough.name .. ' instead'
                choice = nil
                break
            end
        end
    end
    if choice and unset('vulkan-device') then
        apply_gpu('vulkan-device', gpu_choice.tensorrt)
    elseif new_enough and unset('vulkan-device') then
        apply_gpu('vulkan-device', new_enough.uuid)
    end
elseif unset('d3d11-adapter') then
    if gpu_choice.directml then
        apply_gpu('d3d11-adapter', gpu_choice.directml)
    elseif mp.get_property('d3d11-adapter-preference') == 'default' then
        -- Auto: the GPU Windows ranks for high performance rather than adapter 0,
        -- which on a laptop is often the integrated GPU. Player builds without
        -- the option return nil here and keep adapter 0.
        mp.set_property('d3d11-adapter-preference', 'high-performance')
        msg.info('GPU choice: Auto -> d3d11-adapter-preference=high-performance')
    end
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

check_components(trt_unavailable and 'none' or backend, rife_configured, fallback_hint)
