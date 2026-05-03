-- animejanai_seek_bypass.lua
--
-- Bypasses the vapoursynth upscaling filter while the user is seeking, so seeks
-- aren't slowed down by upscaling intermediate frames. Snapshots the active vf
-- at seek start, sets vf="" to bypass, and restores the original vf after a
-- short debounce once seeking stops.
--
-- For keyboard seeks (Right/Left/Up/Down/Ctrl+Right/Ctrl+Left/PGUP/PGDWN) the
-- bypass is applied PRE-EMPTIVELY by wrapping the keybinding itself, before
-- the seek command runs. Without this, the first arrow-key press would still
-- be slow because the `seeking` property observer fires asynchronously and
-- mpv has already started decoding/upscaling frames by then.
--
-- Mouse seeks (drag and click on the seek bar) are caught reactively by the
-- `seeking` observer, which is fast enough for them since drag/click holds
-- seeking=true for long enough that the bypass takes effect well before the
-- seek finishes.
--
-- Toggle on/off at runtime with Ctrl+Shift+j.
--
-- IMPORTANT: the wrapped seek bindings hardcode the same seek seconds as
-- input.conf. If you customise the seek amounts in input.conf, mirror the
-- changes in SEEK_BINDINGS below or this script will silently override them.

local mp = require 'mp'

-- How long to wait after the user's LAST seek-related activity before
-- re-engaging upscaling. Each new seek (key press, mouse drag, mouse click)
-- restarts this window, so rapid-tap arrow seeks stay bypassed throughout
-- the whole burst instead of paying the vapoursynth filter re-init cost
-- between every press. 0.75s is comfortable for human-paced rapid presses
-- (~1-2/sec) without making the post-seek re-engage feel sluggish.
local RESTORE_DELAY_S = 0.75

-- Mirror of input.conf's seek bindings. The script overrides these via
-- add_forced_key_binding so it can bypass the filter BEFORE issuing the seek.
local SEEK_BINDINGS = {
    {key = "RIGHT",      args = {"seek",   "5", "exact"}},
    {key = "LEFT",       args = {"seek",  "-5", "exact"}},
    {key = "UP",         args = {"seek", "-85", "exact"}},
    {key = "DOWN",       args = {"seek",  "85", "exact"}},
    {key = "Ctrl+RIGHT", args = {"seek",  "300", "exact"}},
    {key = "Ctrl+LEFT",  args = {"seek", "-300", "exact"}},
    {key = "PGUP",       args = {"add", "chapter",  "1"}},
    {key = "PGDWN",      args = {"add", "chapter", "-1"}},
}

local saved_vf = nil             -- vf string captured at the start of a seek burst
local restore_timer = nil        -- pending restore (debounce); nil when no restore queued
local self_write_target = nil    -- value of our most recent self-write awaiting echo
local last_activity_time = 0     -- mp.get_time() of most recent seek-related activity
local ignore_initial_seek = false -- suppress on_seeking during mpv's post-load positioning pulse
local initial_seek_timer = nil   -- fallback that clears the flag if no seeking event arrives
local self_seek_in_progress = false -- we issued a seek-0 to force a re-render; ignore its events
local self_seek_clear_timer = nil  -- fallback timer that clears self_seek_in_progress
local enabled = true

-- How long after file-loaded to keep ignoring `seeking` property pulses. Long
-- enough to cover mpv's internal post-load seek (positioning the demuxer at
-- t=0 or the saved-watch-later position), short enough that a real user seek
-- shortly after open isn't suppressed.
local INITIAL_SEEK_GRACE_S = 1.0

local function cancel_restore()
    if restore_timer then
        restore_timer:kill()
        restore_timer = nil
    end
end

local function self_set_vf(value)
    -- Marker so the vf observer can ignore the echo of our own write.
    self_write_target = value
    mp.set_property("vf", value)
end

local function is_upscaling(vf)
    return vf ~= nil and vf ~= "" and vf:find("vapoursynth", 1, true) ~= nil
end

-- Forward decl
local schedule_restore

-- Snapshot vf and set it to "" if upscaling is active. Idempotent within a
-- seek burst (no-op if already bypassed). Always (re)schedules restore from
-- the current time so the debounce window starts fresh on every activity.
local function bypass_now()
    if not enabled then return end
    -- A real user seek takes precedence over any in-flight self-seek-to-rerender.
    if self_seek_clear_timer then
        self_seek_clear_timer:kill()
        self_seek_clear_timer = nil
    end
    self_seek_in_progress = false
    last_activity_time = mp.get_time()
    if saved_vf == nil then
        local cur = mp.get_property("vf", "")
        if is_upscaling(cur) then
            saved_vf = cur
            self_set_vf("")
        end
    end
    schedule_restore()
end

-- Schedule (or re-schedule) the restore for last_activity_time + delay. The
-- timer's callback re-checks activity time when it fires, so any activity
-- that arrives between scheduling and firing pushes the restore back without
-- needing an explicit cancel from every code path.
schedule_restore = function()
    if saved_vf == nil then return end
    cancel_restore()
    local now = mp.get_time()
    local fire_at = last_activity_time + RESTORE_DELAY_S
    local delay = math.max(0.05, fire_at - now)
    restore_timer = mp.add_timeout(delay, function()
        restore_timer = nil
        if saved_vf == nil then return end
        -- A seek is still in progress (typically a long mouse drag — `seeking`
        -- doesn't toggle during continuous drags so last_activity_time hasn't
        -- been refreshed). Don't restore yet; the next seeking=false event
        -- will schedule a fresh restore from that point.
        if mp.get_property_native("seeking") == true then
            return
        end
        -- More activity arrived after we scheduled; postpone again.
        if mp.get_time() < last_activity_time + RESTORE_DELAY_S - 0.01 then
            schedule_restore()
            return
        end
        self_set_vf(saved_vf)
        saved_vf = nil
        -- If paused, mpv won't re-render the current frame after the vf
        -- change on its own — the displayed frame stays un-upscaled and the
        -- vapoursynth filter doesn't initialise until a frame is requested
        -- (i.e. when play resumes), causing visible lag at play time.
        -- Force a re-render of the current frame through the restored chain
        -- so init happens NOW, during pause.
        if mp.get_property_native("pause") == true then
            self_seek_in_progress = true
            if self_seek_clear_timer then self_seek_clear_timer:kill() end
            -- Fallback: clear the marker even if no seeking events fire
            -- (defensive; mpv normally emits seeking true/false for an exact seek).
            self_seek_clear_timer = mp.add_timeout(0.3, function()
                self_seek_clear_timer = nil
                self_seek_in_progress = false
            end)
            mp.commandv("seek", "0", "exact")
        end
    end)
end

local function on_seeking(_, seeking)
    if not enabled then return end

    -- Ignore the seeking events from our own seek-0-to-force-rerender, so
    -- it doesn't recursively trigger another bypass.
    if self_seek_in_progress then
        if not seeking then
            self_seek_in_progress = false
            if self_seek_clear_timer then
                self_seek_clear_timer:kill()
                self_seek_clear_timer = nil
            end
        end
        return
    end

    -- Skip mpv's internal post-load seek pulse. Keyboard pre-emptive bypass
    -- still works in this window because it doesn't go through this handler.
    if ignore_initial_seek then
        if not seeking then
            -- The initial seeking-true→false pulse has completed; subsequent
            -- seeks are real user input.
            ignore_initial_seek = false
            if initial_seek_timer then
                initial_seek_timer:kill()
                initial_seek_timer = nil
            end
        end
        return
    end

    if seeking then
        -- Reactive bypass for non-keyboard seeks (mouse drag/click on the
        -- seek bar, MPRIS, etc). Keyboard seeks have already bypassed via
        -- their wrapped bindings; this just refreshes the activity window
        -- (and is what catches mouse-driven seeks initially).
        bypass_now()
    elseif saved_vf ~= nil then
        -- Seek ended. Make sure a restore is queued. (bypass_now already
        -- queued one, but seek-end is a useful "definitely no more frames
        -- coming for this seek" signal so we re-arm with fresh timing.)
        schedule_restore()
    end
end

local function on_vf_changed(_, value)
    -- Echo of our own write: consume the marker and ignore.
    if self_write_target ~= nil and value == self_write_target then
        self_write_target = nil
        return
    end
    self_write_target = nil

    -- External (user) vf change. Only relevant if we're mid-bypass.
    if saved_vf == nil then return end

    if is_upscaling(value) then
        -- User picked a different upscale profile mid-seek. Adopt it as the
        -- new restore target and re-bypass for the remainder of the seek.
        saved_vf = value
        self_set_vf("")
    else
        -- User explicitly disabled upscaling (apply-profile upscale-off).
        -- Drop the saved state so we don't override their choice on seek-end.
        -- NOTE: if the user presses upscale-off while we already have vf=""
        -- bypassed, mpv won't fire this observer (no value change) and we'll
        -- restore upscaling on seek-end. Press the off key again after the
        -- seek to recover.
        cancel_restore()
        saved_vf = nil
    end
end

local function reset_state()
    cancel_restore()
    saved_vf = nil
    self_write_target = nil
    if initial_seek_timer then
        initial_seek_timer:kill()
        initial_seek_timer = nil
    end
    ignore_initial_seek = false
    if self_seek_clear_timer then
        self_seek_clear_timer:kill()
        self_seek_clear_timer = nil
    end
    self_seek_in_progress = false
end

local function on_file_loaded()
    -- New file: any in-flight bypass from the previous file is no longer valid.
    -- mpv will apply the [default] profile from mpv.conf which sets vf normally.
    reset_state()

    -- Suppress the next `seeking` pulse — it's mpv's internal post-load seek
    -- (positioning the demuxer at t=0 or the saved-watch-later position), not
    -- a user-initiated seek. Without this, upscaling would briefly bypass for
    -- RESTORE_DELAY_S at the start of every newly-opened video.
    ignore_initial_seek = true
    initial_seek_timer = mp.add_timeout(INITIAL_SEEK_GRACE_S, function()
        initial_seek_timer = nil
        ignore_initial_seek = false
    end)
end

local function toggle()
    enabled = not enabled
    if not enabled and saved_vf ~= nil then
        -- Restore immediately if we're disabling mid-bypass so the user isn't
        -- left with no upscaling while seeks are flying around.
        cancel_restore()
        self_set_vf(saved_vf)
        saved_vf = nil
    end
    mp.osd_message("AnimeJaNai seek bypass: " .. (enabled and "ON" or "OFF"))
end

mp.observe_property("seeking", "bool", on_seeking)
mp.observe_property("vf", "string", on_vf_changed)
mp.register_event("file-loaded", on_file_loaded)
mp.register_event("end-file", reset_state)

-- Override the keyboard seek bindings so the bypass runs BEFORE the seek
-- command — eliminates the first-press latency caused by the seeking-property
-- observer firing too late.
for _, binding in ipairs(SEEK_BINDINGS) do
    local name = "animejanai_bypass_" .. binding.key:lower():gsub("[^%w]", "_")
    local args = binding.args
    mp.add_forced_key_binding(binding.key, name, function()
        bypass_now()
        mp.commandv(unpack(args))
    end, {repeatable = true})
end

mp.add_key_binding("Ctrl+Shift+j", "toggle_seek_bypass", toggle)
