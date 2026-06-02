-- Applies user keybindings from portable_config/input-user.conf on top of the shipped input.conf.
-- mpv's input.conf has no "include" mechanism (unlike mpv.conf), so instead we read the user file
-- and register each binding at runtime via the "keybind" command. Runtime binds override the
-- shipped defaults, and input-user.conf is never overwritten by updates - the keybinding parallel
-- of mpv-user.conf.
--
-- Same syntax as input.conf: one "KEY  command" per line; "#" starts a comment.

local mp = require 'mp'
local msg = require 'mp.msg'

local path = mp.command_native({ "expand-path", "~~/input-user.conf" })
local f = io.open(path, "r")
if not f then
    return
end

for line in f:lines() do
    -- Drop trailing comments (and the "#menu:" annotations input.conf allows), then trim.
    line = line:gsub("%s*#.*$", ""):gsub("^%s+", ""):gsub("%s+$", "")
    if line ~= "" then
        local key, cmd = line:match("^(%S+)%s+(.+)$")
        if key and cmd then
            mp.command_native({ "keybind", key, cmd })
        else
            msg.warn("Ignoring malformed line in input-user.conf: " .. line)
        end
    end
end

f:close()
