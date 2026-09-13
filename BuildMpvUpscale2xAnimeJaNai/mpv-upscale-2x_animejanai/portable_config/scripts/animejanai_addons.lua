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

mp.command_native_async({
    name = 'subprocess',
    args = { host, 'player', root, data, tostring(mp.get_property_number('pid')) },
    detach = true,
    playback_only = false,
}, function(success, result)
    if not success or not result or result.status ~= 0 then
        msg.warn('Could not connect addon lifecycle. Playback continues normally.')
    end
end)
