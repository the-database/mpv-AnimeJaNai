# Player and Windows sign-in activation

The host consolidates `manual`, `on_manager`, `on_player` and `on_login` reasons
into one worker per installed addon. An addon receives one `start` event with
the triggering reason. Removing one reason does not interrupt other owners;
the last release stops the worker and drains its owned resources. Pings do not
restart a stopped or failed addon. Manager's Stop clears current reasons; a new
declared activation or an explicit manual start can start it again.

## Player attachment

The integrated build includes `portable_config/scripts/animejanai_addons.lua`
and `addon-host/ajn-addon-launcher.exe`. The script starts the launcher
asynchronously, detached from playback. It does not load third-party code into
mpv, transmit media filenames, export player IPC, or grant media access. The
launcher opens a lifecycle-only connection to the host. Source/profile,
frame-sample and output permissions still require their own approvals.

The bridge holds the original player's process handle, checks that its image is
the `mpvnet.exe` or `mpv.exe` in this installation, and closes the connection when that process
ends. PID reuse cannot transfer its lifetime to another process. Two players
share one addon worker but retain separate activation reasons. A killed player
releases its reason without stopping another player or manually started work.

With no installed addons, the script launches no helper or host. Installing the
first addon while a player is already open requires reopening that player. If
a lifecycle connection already exists, installs and rollbacks acquire the
applicable connected triggers immediately. The default data directory is
`<AJN root>/animejanai/addons`; the trusted `ANIMEJANAI_ROOT` and
`ANIMEJANAI_DATA_DIR` overrides match Manager's layout.

The bridge pings every five seconds and makes at most three connection/start
attempts if the whole host connection is lost. It never blocks playback on an
addon callback. Manager and players use the same launch-or-connect helper;
simultaneous host candidates respect the exclusive data-directory lease and
connect to the winner. The existing eight-client limit includes lifecycle
connections. Each open player with installed addons has a trusted .NET helper
process; no claim of zero memory overhead is made for this active case.

## Optional Windows startup

Manager's **Login startup** dialog is off by default. Saving an enabled choice
registers the console-free launcher for the current Windows user. Only addons
declaring `on_login` activate at the next sign-in. Registration is independent
of permission grants and cannot be changed through the guest API. Enabling it
does not start a second copy of an addon already running for another reason.

There is one named value under `HKCU/Software/Microsoft/Windows/CurrentVersion/Run`
per addon data directory, with the name derived from the same canonical path
identity as the private host endpoint. Other applications' entries are untouched.
That value is the persistent choice; there is no second file whose saved flag
could disagree with Windows registration. A moved installation is shown in the
dialog and replaced or removed only when the user saves their choice.

The launcher derives the installation from its own location and omits the
standard data directory from its command. Custom data paths are quoted using
Windows argument rules. Commands exceeding Windows' documented 260-character
Run limit are rejected before registration. See Microsoft's [Run key
documentation](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys)
and [argument quoting rules](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-commandlinetoargvw).
Windows may delay startup or separately disable it in Startup apps.

Disabling in Manager removes the owned entry and releases login reasons from
the live host. Other active reasons continue. Removing the entry outside Manager
is noticed on the next five-second ping. A per-data-directory file lease prevents
duplicate login helpers. The helper exits immediately without creating a host
when its exact startup entry is absent. No setup script or first-run path enables
startup automatically.

Launcher failures write one bounded `launcher-error.txt` in addon data and exit;
they do not become unhandled .NET crash dialogs. The host still idles out roughly
30 seconds after its last client and worker stop. An active login connection
deliberately keeps it available for the user's session.

## Validation and limits

The contract suite checks mixed activation, fixed client roles, permission
separation, install/rollback during playback, stopping, failed registration,
path quoting, missing launchers and preserving other owners during opt-out.
Startup mutation tests use an in-memory registry adapter, so running the tests
does not register real Windows startup. Manager tests render and exercise its
off-by-default checkbox, cancellation, save/reopen and removal against a fixture.

The optional native suite's `--lifecycle-only` mode runs two actual packaged mpv
processes and real Wasm workers. `--lifecycle-mpvnet-only` runs the same checks
through the default mpv.net launcher with independent process instances. Both
check normal playback with a failing addon,
forced and normal player exits, shared/manual lifetimes, persistent private
data, empty-installation behavior, the GUI launcher subsystem, no-opt-in exit,
and complete helper/idle-host cleanup. Run it with a unique test installation
which no other test is using. Windows sign-out/sign-in and OS Startup-apps
interaction remain manual acceptance checks.

This is lifecycle integration, not a player-observation or frame subscription
capability. Shared-player frames, final-display/HDR stages and Linux lifecycle
enforcement remain separate integration work.
