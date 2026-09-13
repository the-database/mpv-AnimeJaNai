-- Trusted AJN lifecycle bridge. It never loads addon code into mpv. The isolated
-- host keeps one instance per addon and releases this player's activation when
-- its original process exits (including an unexpected exit).
local mp = require 'mp'
local utils = require 'mp.utils'
local msg = require 'mp.msg'

if mp.get_property('platform') ~= 'windows' then return end
local root = os.getenv('ANIMEJANAI_ROOT') or mp.command_native({ 'expand-path', '~~/../' })
local data = utils.join_path(os.getenv('ANIMEJANAI_DATA_DIR') or utils.join_path(root, 'animejanai'), 'addons')
local host = utils.join_path(root, 'addon-host/ajn-addon-launcher.exe')
if not utils.file_info(host) then return end

-- No installed addons means no helper, host or runtime overhead. Installing
-- the first addon while a player is already open takes effect on its next launch.
local installed = utils.join_path(data, 'installed')
local registered = false
for _, name in ipairs(utils.readdir(installed, 'dirs') or {}) do
    if utils.file_info(utils.join_path(utils.join_path(installed, name), 'active.json')) then
        registered = true
        break
    end
end
if not registered then return end

-- Only this trusted script configures the private sample filter. The file
-- carries bounded configuration, never pixels or arbitrary player commands.
local pid = mp.get_property_number('pid')
local instance = string.format('%d-%.0f', pid, mp.get_time() * 1000)
local control = utils.join_path(data, 'player-control/' .. instance .. '.json')
local label = 'ajn-player-sample'
local revision, changed, mapping = nil, 0, nil
local function tap_present()
    for _, item in ipairs(mp.get_property_native('vf') or {}) do
        if item.label == label then return true end
    end
    return false
end
local function remove_tap()
    if tap_present() then mp.commandv('vf', 'remove', '@' .. label) end
    mapping = nil
end
local function update_sample()
    local file = io.open(control, 'rb')
    local text = file and file:read(4097)
    if file then file:close() end
    local value = text and #text <= 4096 and utils.parse_json(text) or nil
    if type(value) ~= 'table' or value.schemaVersion ~= 1 or value.instance ~= instance
        or type(value.revision) ~= 'string' or #value.revision > 20
        or not value.revision:match('^%d+$') then remove_tap(); return end
    if revision ~= value.revision then revision = value.revision; changed = mp.get_time() end
    if value.available ~= true or value.enabled ~= true or mp.get_time() - changed > 3 then
        remove_tap(); return
    end
    local prefix = 'Local\\AJN.PlayerFrames.'
    local name = value.mapping
    if type(name) ~= 'string' or #name ~= #prefix + 32 or name:sub(1, #prefix) ~= prefix
        or not name:sub(#prefix + 1):match('^[a-f0-9]+$') then remove_tap(); return end
    if mapping ~= name then remove_tap(); mapping = name end
    if not tap_present() then
        mp.commandv('vf', 'add', '@' .. label .. ':ajn-sample:name=%' .. #name .. '%' .. name)
    end
end
mp.add_periodic_timer(0.5, update_sample)

mp.command_native_async({
    name = 'subprocess',
    args = { host, 'player', root, data, tostring(pid), instance },
    detach = true,
    playback_only = false,
}, function(success, result)
    if not success or not result or result.status ~= 0 then
        msg.warn('Could not connect addon lifecycle. Playback continues normally.')
    end
end)
