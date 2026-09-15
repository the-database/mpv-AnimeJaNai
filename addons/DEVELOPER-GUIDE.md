# AnimeJaNai Addon Developer Guide

**Development preview:** API 1.8 scene detection has passed protocol tests and short DirectML/RIFE playback checks. TensorRT scene playback and broad performance qualification are still outstanding; use the matching build report for test scope.

**Edition:** 2026-09-15 · **Addon API:** 1.8 preview · **Platform:** Windows x64

Create, build, install, test and distribute an AnimeJaNai (AJN) addon using this
document and the matching release tools. No previous conversation, maintainer
handoff or checkout of AJN's repositories is required. The complete JavaScript
API declarations and ten feature examples are included in this file.

This guide targets the **API 1.8 development preview**. API 1.8 remains a
preview, so it is suitable for developing and testing addons but is not yet a
frozen community compatibility promise. Use the guide shipped with your release.
The addon API version, AJN application version and your addon's version are
different numbers with different purposes.

You still need to describe what your addon should do. Device protocols, service
APIs, accounts and hardware belonging to an outside product are not defined by
AJN; obtain that product's documentation when implementing an integration.

### Reading paths

- **First addon:** read sections 1–4, complete the greeting tutorial, then use
  sections 12–14 to test and share it.
- **Video, streaming or lighting-related work:** complete the tutorial first,
  choose the feature in section 5, then read the corresponding sections 6–10
  and copy a complete example from Appendix B.
- **Using an LLM:** attach this entire file and use the prompt in section 15.
  All AJN contracts needed for the documented features are embedded here.
- **Installing someone else's addon:** use section 3. No compiler or separate
  server account is needed for installation.

### Contents

1. [What an addon is and what this release supports](#overview)
2. [Build your first addon](#tutorial)
3. [Install, approve and operate an addon](#users)
4. [Manifest, settings, actions and saved data](#manifest)
5. [Choose capabilities and permissions](#recipes)
6. [Lifecycle, timers and resource ownership](#lifecycle)
7. [Independent processing sessions](#sessions)
8. [Image samples and normal-player observations](#frames)
9. [HTTP, UDP and credentials](#network)
10. [Remote media and encoded output](#media-io)
    - [HTTP serving, client credentials and native stream delivery](#streaming)
11. [Complete API and limits](#api)
12. [Developer command reference and replay](#cli)
13. [Testing and troubleshooting](#testing)
14. [Versioning and publishing your addon](#publishing)
15. [Instructions for an LLM-assisted project](#llm)
A. [Complete JavaScript editor declarations](#sdk)
B. [Complete feature examples](#examples)
C. [Release compatibility and documentation provenance](#provenance)
D. [Custom RIFE scene detection and complete example](#scene-detection)


<a id="overview"></a>

## 1. What an addon is and what this release supports

An AJN addon is JavaScript compiled to a portable WebAssembly (Wasm) module.
AJN runs it in an isolated worker and supplies the `ajn` API. Your addon declares
settings and action buttons; Manager renders them and asks the user to approve
access. You distribute one `.ajnaddon` file.

```text
Your source: addon.js + manifest.json (+ ajn.d.ts for editor help)
                 ↓ AJN build command and the Javy compiler
Your package: one .ajnaddon archive
                 ↓ Manager installs it after permission review
Your worker: onEvent(event, ajn) → documented AJN operations
```

The host is a local AJN component which Manager starts automatically. Addon
users do not connect to a remote host or enter an address to install a package.
An approved network destination is a service/device the addon communicates with,
not the addon installation mechanism.

| Available in this preview | Conditions |
| --- | --- |
| Settings, action buttons, private saved state, timers | No GPU needed; private storage requires its own grants |
| Independent local-video processing | Approved file/profile and a matching full AJN runtime |
| Small images from owned sessions or ordinary playback | Progressive mono SDR DirectML/D3D11 path; separate grants |
| HTTP/HTTPS control requests and outgoing UDP | Explicit approved destinations and bounded payloads |
| Saved request-header credentials | User stores them in Manager; addon requests scoped use |
| HTTP/HTTPS remote video input | Approved source service and profile; single supported media resource |
| Encoded video/audio upload to HTTP/HTTPS | Approved receiver, matching native adapter and supported NVIDIA NVENC hardware |
| HTTP(S) listeners, proxying and WebSocket tunnels | Separately reviewed listener/upstream access and short asynchronous callbacks |
| Client credential delegation | Opaque per-client/upstream contexts; addon validates authorization |
| Probe, selected tracks/offsets, subtitles and served video/audio | Matching API 1.7 runtime and explicit source/profile/listener approvals |

No public API currently provides custom HTML/native UI, arbitrary file access,
native DLL loading, process launching, raw mpv commands,
general TCP sockets, broadcast/multicast discovery or automatic
OAuth/browser login. Remote media does not traverse HLS/DASH playlists. Image
samples exclude HDR, CUDA/TensorRT and final display composition. A requirement
outside this list may need a future framework capability; do not invent one.

This is plain JavaScript inside Javy, **not Node.js or a web browser**. There is
no `require`, DOM, `fetch`, filesystem module or JavaScript scheduling API.
Keep `onEvent` synchronous; use AJN timers and operation handles for ongoing work.
No npm package manager or TypeScript compiler is required. `ajn.d.ts` supplies
editor/type information; it is not executable code. Third-party JavaScript must
be compatible with this runtime and fit into the source you compile.

Wasm has no general filesystem/network authority. Broker permissions and
resource approvals mediate operations; native video remains in trusted AJN
components. Isolation has resource costs and does not make a GPU driver or
codec infallible. A package hash checks integrity, not publisher identity.
Catalog signing and automatic addon updates are not implemented in this release.


<a id="tutorial"></a>

## 2. Build your first addon

Download and extract **one** matching release asset into a writable folder:

| Release asset | Use it for |
| --- | --- |
| `AnimeJaNai-3.6.1-addons.integration.2-win-x64.zip` | Recommended: Manager, actual playback/native testing and developer tools |
| `AnimeJaNai-Addon-Foundation-integration.2-win-x64.zip` | Standalone host/SDK tutorial; native playback needs the full AJN build |

Get the assets from the GitHub release carrying this guide. Do not use GitHub's
automatic **Source code** download as a substitute for these executable bundles.
Extract the whole archive and keep its directory layout intact. Neither a .NET
SDK nor a native compiler is needed when using these bundles; the host is
self-contained. Internet access is needed for the initial compiler bootstrap.

Open **PowerShell 7** in the extracted folder. Run `$PSVersionTable.PSVersion`
and check that Major is at least 7; Windows PowerShell 5.1 is a different shell.
Use a text editor that saves UTF-8. All tutorial paths below are relative to
that extracted folder, and all later command blocks use the variables established
here in the same PowerShell session. If you reopen the shell, set them again.

Choose the setup block for your bundle; do not run both. Run each command block
in order. Edit the files when instructed; the JSON/JavaScript blocks are file
contents, not PowerShell commands. Choose a new working folder if repeating
the tutorial, because source/package overwrite protection is intentional.

Build a small addon, run it, save its settings, and package an update. This guide
uses **plain JavaScript** and AJN API 1.7's supported API 1.0 subset. The tutorial
needs no GPU or inference engines. Video features can be added afterwards using
the same package and permission model.

AJN compiles JavaScript to portable WebAssembly. It is not Node.js or a browser:
there is no `require`, `fetch`, DOM, filesystem access, process launching or
`setInterval`. Call the AJN SDK for supported operations. Callbacks finish
synchronously; use AJN timers and session/request handles for continuing work.

### 1. Prepare a workspace

Use Windows x64 and **PowerShell 7**. Choose one starting point:

- **Standalone developer bundle:** extract the matching
  `AnimeJaNai-Addon-Foundation-…-win-x64.zip`. It contains the ready-to-run CLI,
  runtime, SDK, source, examples and guides. Open PowerShell in that folder.
- **Full AJN addon preview:** open PowerShell in the folder containing
  `AnimeJaNaiManager.exe`. Use the alternate path setup below.

For the standalone bundle:

```powershell
$ajnExe = (Resolve-Path ./host/ajn-addon.exe).Path
$runtime = (Resolve-Path ./runtime/wasmtime.exe).Path
./tools/bootstrap.ps1 -ToolsDirectory ./addon-work/tools
$tools = Get-Content ./addon-work/tools/tools.json -Raw | ConvertFrom-Json
```

For the full AJN preview, use these commands **instead**:

```powershell
$ajnExe = (Resolve-Path ./addon-host/ajn-addon.exe).Path
$runtime = (Resolve-Path ./addon-host/runtime/wasmtime.exe).Path
./addon-development/source/tools/bootstrap.ps1 -ToolsDirectory ./addon-work/tools
$tools = Get-Content ./addon-work/tools/tools.json -Raw | ConvertFrom-Json
```

Bootstrap downloads pinned, checksum-verified Javy and Wasmtime tools. Javy is
needed to build your JavaScript. The full/standalone bundles already contain the
runtime used to run it. End users do not need to perform this developer setup.

Use this helper so a failed CLI command stops the instructions rather than
silently continuing:

```powershell
function Invoke-Ajn {
    & $ajnExe @args
    if ($LASTEXITCODE -ne 0) { throw "AJN command failed. Read the error above." }
}
Invoke-Ajn new ./addon-work/my-greeting org.example.greeting
```

The new folder contains `addon.js`, `manifest.json` and `ajn.d.ts` for editor
completion. The command deliberately refuses an existing folder. Use a new
folder if you are repeating the tutorial. Keep source files under version control;
keep test data and compiled packages outside that source folder.

### 2. Define the addon

Replace `addon-work/my-greeting/manifest.json` with this complete manifest:

<!-- tutorial:manifest -->
```json
{
  "schemaVersion": 1,
  "id": "org.example.greeting",
  "name": "Friendly greeting",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 0 },
  "permissions": ["storage.read", "storage.write"],
  "requiredCapabilities": { "settings": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "greeting": { "type": "string", "label": "Your greeting", "description": "Shown when you choose Show greeting.", "default": "Hello from my addon!", "maxLength": 120 }
  },
  "actions": {
    "greet": { "label": "Show greeting" },
    "remember": { "label": "Remember greeting", "description": "Save this greeting and increase its saved count." }
  }
}
```

The ID is your addon's permanent identity: lowercase reverse-domain text, for
example `org.yourname.greeting`. Keep it unchanged across updates. Different IDs
have different data and registrations. Use your own ID when distributing your
work. `name` is the friendly title; `version` describes your package release.

The API requirement is separate from the AJN application version. This addon
only needs API 1.0 plus settings capability 1.0. Increasing a package version
does not require increasing the API requirement. The builder adds the module
digest; you do not write or update `moduleSha256` yourself.

### 3. Write the behavior

Replace `addon-work/my-greeting/addon.js` with:

<!-- tutorial:javascript -->
```javascript
/// <reference path="./ajn.d.ts" />
// @ts-check

/** @param {AjnEvent} event @param {AjnApi} ajn */
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Ready. Choose Show greeting." };
    if (event.name === "settings.changed") return { message: "Settings saved." };
    if (event.name !== "action") return null;

    const greeting = String(ajn.settings.get().greeting);
    const action = /** @type {{ id: string }} */ (event.data).id;
    if (action === "greet") return { message: greeting };
    if (action !== "remember") return null;

    try {
        const previous = /** @type {{ count?: number } | null} */ (ajn.storage.get("greetingHistory"));
        const count = (previous && typeof previous.count === "number" ? previous.count : 0) + 1;
        ajn.storage.set("greetingHistory", { count, greeting });
        return { message: "Saved greeting " + count + ": " + greeting };
    } catch (error) {
        if (error.code === "permission_denied") {
            return { message: "To remember greetings, reinstall this package and allow reading and saving its own data." };
        }
        throw error;
    }
}
```

Manager renders a returned string or `{ "message": "…" }` as a readable action
result. Other JSON is shown as formatted diagnostic data. There is no addon HTML
or custom native UI in this preview. Labels, descriptions, settings and actions
in the manifest are the supported way to build the interface.

This example handles denied storage access without crashing. Its greeting still
works without storage. An unknown failure is rethrown so the runtime can stop
the addon and report it. The count and greeting are saved together as one atomic
JSON value. Keep related fields in one value when they must change together;
separate writes are not a transaction.

### 4. Build and test the package

```powershell
Invoke-Ajn build ./addon-work/my-greeting $tools.javy.path ./addon-work/greeting-0.1.0.ajnaddon
Invoke-Ajn inspect ./addon-work/greeting-0.1.0.ajnaddon
Invoke-Ajn install-dev ./addon-work/greeting-0.1.0.ajnaddon ./addon-work/test-data 'storage.read,storage.write'
Invoke-Ajn run org.example.greeting ./addon-work/test-data $runtime
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime greet
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime remember
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime remember
```

You should see the greeting, then **Saved greeting 1**, then **Saved greeting 2**.
Each CLI invocation is a new process. Seeing 2 proves the saved count survives
restarting. `install-dev` explicitly grants exactly the listed permissions to
that package. Omitting the list grants none.

Keep this CLI test directory separate from a running AJN installation's addon
data. The CLI and persistent runtime do not concurrently own the same directory.
The resulting `host_running` error means the runtime already owns it;
use Manager for that installation or a separate CLI test directory.

Change a setting and verify the result:

```powershell
@{ greeting = 'Good evening!' } | ConvertTo-Json | Set-Content ./addon-work/greeting-settings.json -Encoding utf8NoBOM
Invoke-Ajn configure org.example.greeting ./addon-work/test-data ./addon-work/greeting-settings.json
Invoke-Ajn settings org.example.greeting ./addon-work/test-data
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime greet
```

The result should say **Good evening!**. Settings are owned by the user: addons
read them through `ajn.settings.get()` and cannot change them. Private storage
is for an addon's own state, caches and preferences that are not user settings.

Now install the built file through Manager's **Addons → Install addon…**, review
its permissions, choose **Start addon**, and use its actions. Manager has its
own saved data; it does not inherit the separate CLI test directory's count.
Check the interface with no permissions too: Show greeting should still work,
and Remember greeting should explain what is missing.

### 5. Package an update

Change the manifest version to `0.2.0`, make your source change, and build to a
new filename. Do not replace a package while someone is reviewing it.

```powershell
Invoke-Ajn build ./addon-work/my-greeting $tools.javy.path ./addon-work/greeting-0.2.0.ajnaddon
Invoke-Ajn install-dev ./addon-work/greeting-0.2.0.ajnaddon ./addon-work/test-data 'storage.read,storage.write'
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime remember
Invoke-Ajn rollback org.example.greeting ./addon-work/test-data
Invoke-Ajn action org.example.greeting ./addon-work/test-data $runtime greet
```

The counter continues and the greeting stays changed. Updating and rollback
retain settings/private storage. Test both directions before distributing an
update. A package cannot restore old data formats by itself: add a schema field
inside your own stored values and keep readers compatible, or explicitly handle
unsupported versions. AJN runs no automatic migration/setup scripts.

Distribute the `.ajnaddon` file plus a short description of supported AJN/API
versions, permissions, setup steps, hardware needs and known limits. A package
contains exactly the manifest and portable Wasm module. Do not include native
executables, libraries or installation scripts. This preview supports reviewed
local packages; catalog signing and automatic updates are future work.

### Choose the right extension API

| You want to… | API and required capability | Permissions / user choices |
| --- | --- | --- |
| Show settings and actions | `settings.get`, event callback; settings 1.0 | Manifest definitions; no added permission |
| Save addon state | `storage.get/set` | `storage.read/write` |
| Do periodic work | `timers.set/clear`; timers 1.0 | No added permission; use serialized timer events |
| Process independent videos | `sessions`; sessions 1.1 | `sessions.manage`, approved files/profiles |
| Read small samples from an owned session | `frames`; frames 1.0 | `frames.read` + `sessions.manage` |
| Observe normal AJN playback | `playerFrames`; playerFrames 1.0, API 1.6 | `player.observe` + `frames.read`; no playback controls |
| Customize RIFE scene decisions | `sceneDetection`; 1.0, API 1.8 | `player.sceneDetection` + `frames.read`; see Appendix D |
| Talk to a service/device | `network`; network 1.0 | `network.connect`, approved destination; optional `credentials.use` |
| Process a remote video | `sessions.openRemote`; remoteSources 1.0, API 1.5 | `media.input` + `sessions.manage` + `network.connect`, approved service/profile |
| Deliver encoded video/audio | `outputs`; outputs 1.0, API 1.4 | `media.output` + session/network grants, approved input/profile/receiver; remote input adds its own requirements |
| Receive client requests | `httpServer`; httpServer 1.0, API 1.7 | `network.listen`, reviewed listener and HTTPS certificate where applicable |
| Forward HTTP/WebSockets | `httpProxy`; httpProxy 1.0, API 1.7 | `network.proxy` + `network.connect`, approved upstream; forwarding also uses a listener |
| Probe and serve processed media | `mediaProbe`, `mediaStreams`, `outputPlayback`; API 1.7 | Source/profile, input/output/session grants, plus listener and remote-source grants as needed; see `STREAMING.md` |
| Use a client's upstream credential | `requestCredentials`; API 1.7 | `credentials.delegate` + `credentials.use`, declared sensitive fields and approved destination; addon must validate the client |
| Burn or extract subtitles | `outputPlayback`, `subtitles`; API 1.7 | Selected source/track, explicit external resource approvals and the matching native capability |

Read [Recipes and examples](#recipes), [API reference](#api) and
[SDK types](#sdk). Each feature guide documents its exact limits. Do not
infer a native feature from the application version alone: `ajn.info()` reports
optional capabilities and granted permissions. Required capabilities belong in
the manifest; optional ones need a runtime check and a useful fallback.

### Lifecycle and time budgets

The same running worker handles `start`, `action`, `settings.changed`, `timer`
and `stop` events one at a time. Several players/Manager windows can hold startup
reasons for the same addon without creating duplicate workers. `start` is not a
notification for each individual player. Use `playerFrames.list()` for attached
players. [Lifecycle](#lifecycle) specifies startup and stop behavior.

Callbacks have a two-second deadline, including SDK calls. Do not wait in a loop
for an engine build, completed request or video frame. Open a session/request,
retain its opaque ID in memory, return, and poll status from a timer. `null` from
a frame read means no new sample; it is not a black frame or an error.

Use `ajn.timers`, not promises, `setTimeout`, or `setInterval`. Missed timer ticks
coalesce. Keep callbacks small, cap your own queued work, and inspect errors or
terminal states. Check ownership before using an ID: IDs belong to the worker
that created them. Do not save active IDs in persistent storage for reuse after
restart. On stop, request cleanup promptly; AJN also cleans up owned resources
when a worker exits or fails.

Manager actions now require a running addon. The developer CLI's one-shot
`action` command intentionally performs start/action/stop in one invocation.
Use Manager or the replay command when testing a sequence that retains native
sessions. Never assume an isolated CLI action will leave background work running.

### Make an addon easy to use

- Give actions concrete names such as “Refresh library” and “Stop observing”.
  Explain prerequisites in their descriptions and return a readable result.
- Request only permissions your features use. Explain denied access and empty
  resource lists; never assume the user checked every box.
- Keep startup lightweight. Do not send data to a service merely to populate
  settings. Make ongoing background work clear and easy to stop.
- Prefer saved settings over hardcoded machine paths, player internals or device
  addresses. Store IDs only while they are live and re-enumerate resources.
- Preserve unknown saved fields and be careful with newer data during rollback.
- Treat video PTS, color space and sample stage as defined contracts. “Processed”
  samples exclude final display composition and are not display-time measurements.

### Test before sharing

Check first install, no permissions, partial permissions, empty resource lists,
normal operation, a closed player/service, repeated start/stop, changed settings,
an update, rollback and restart. Confirm one addon's failure does not stop other
work. Hardware-dependent addons need actual playback tests on supported hardware;
a passing JavaScript build does not validate a media pipeline.

The maintained tutorial check extracts this guide's manifest and JavaScript,
compiles and runs them, checks denied permissions, saved state, settings, update
and rollback. It is [tools/test-tutorial.ps1](https://github.com/422415/mpv-AnimeJaNai/blob/5b980a033cf50e663e5e51c046ed3c48da8ca9b5/addons/tools/test-tutorial.ps1). The host's
separate suite also runs an unchanged older API 1.0 binary. Keep those old-client
fixtures when adding a feature; do not silently change existing field meanings.

For an LLM-assisted implementation, provide this guide, the API reference, the
relevant feature guide and `ajn.d.ts`. Ask it to use only those documented APIs,
keep callbacks synchronous and bounded, explain every requested permission,
handle unavailable capabilities, and supply reproducible build/test steps. Check
its output for invented Node/browser APIs and undocumented native shortcuts.

See [Troubleshooting](#testing) for error codes and recovery. API 1.7 is
still a preview contract; public stability and deprecation policy must be frozen
before a community release.


<a id="users"></a>

## 3. Install, approve and operate an addon

Addons add features to AJN. This Windows developer preview installs `.ajnaddon`
files that a creator gives you. Website installation and automatic addon updates
are planned for later.

You need the **complete AJN addon preview**, extracted into a writable folder.
Open the `AnimeJaNaiManager.exe` inside that folder. Installing and using addons
does not require programming tools, .NET SDK, or a separate server account.

### Install your first addon

1. Open **Addons**. AJN prepares its local addon support automatically.
2. Choose **Install addon…** and select the creator's `.ajnaddon` file.
   Keep the file intact; do not unzip it or copy its contents into AJN.
3. Read the package name, version and requested permissions. Check the access
   you want to allow, then choose **Install**. Nothing is checked for you.
4. The installed addon becomes selected. If it is stopped, choose **Start addon**.
5. Adjust its settings, choose **Save settings**, and use its action buttons.

Some addons start automatically with Manager or a player. Their declared startup
events determine this. After installing your first addon, reopen any video that
was already playing so that player can participate.

Local packages currently have no verified publisher identity. A package's name
alone does not prove who made it. Permission review applies to the exact file
you selected.

### Understand permissions and access

Only checked permissions are allowed. An addon may still install with a
permission unchecked, but a feature needing it may be unavailable. To change
your choices in this preview, use **Install addon…** with the same package again
and review the permissions anew. This restarts that addon and keeps its saved
settings and data.

Some permissions also require a specific resource to be approved:

| What the addon needs | Where you choose what it can use |
| --- | --- |
| Its own saved data | Installation permission review; it cannot use another addon's private data |
| A local video | **Processing access → Allow a media file** |
| Your processing settings | **Processing access → Allow a saved profile**; save profile edits first |
| Images from normal AJN playback | Both image-sample and normal-player-observation permissions during installation |
| A service or device | **Service and device access → Allow a service or device** |
| A saved service credential | The specific approved service's **Save credential** control, plus the credential permission |
| Remote video input or processed video output | Their separate install permissions, an approved service, and an approved processing profile |

Approving a saved profile gives the addon a copy of those settings. Later profile
edits do not silently change the copy. Approve another saved copy when needed.
Removing media/service access stops the addon and its work. Reapprove only the
access you want, then start it again. Changing or removing a saved credential
also stops the addon so old access cannot continue unnoticed.

### Start, stop, save and close

- **Start addon** starts it manually. It can keep working after Manager closes.
  Choosing Start on an already running compatible addon also keeps it running
  independently of Manager. **Stop addon** ends its current work.
- Actions are available while the addon is running. This lets multi-step actions
  retain their sessions and observations between clicks.
- **Save settings** stores your edits. Switching addons will remind you to save
  first. **Reload** lets you explicitly discard unsaved changes. Closing Manager
  also asks before discarding unsaved addon settings; Cancel keeps your draft.
- Stopping an addon does not uninstall it. A later declared startup event, such
  as opening another player, can start it again. Remove it to prevent future use.
- A stopped or failed addon is not repeatedly restarted by a timer. Read its
  message and choose Start when you are ready to retry.

**Performance limits** controls how many background video-processing sessions
all addons can run together: 1–16, default 2. Raising the limit allows more work
and can increase GPU/memory use. Lowering it leaves existing sessions running
and limits new ones. An addon's own stream-count setting may also need adjustment.
This limit is separate from ordinary player observations and worker limits.

**Windows startup** is optional and off by default. Enable it only if you want
addons that support sign-in activation to start when you sign in. Turning it off
ends sign-in activation; addons started manually or in use by a player can remain
running. The setting applies to your Windows account and this AJN installation.

### Update, restore or remove

Install the newer `.ajnaddon` file to update an addon. It should use the same
addon ID. Review the new package and permissions; settings and private data are
retained. Check the access sections too: resource approvals are tied to the exact
package version and may need review again.

**Previous version** asks before restoring the last installed package and its
previous permission choices. Saved settings/data are kept, so a creator must make
data changes compatible with rollback. If a saved setting is incompatible,
Manager explains which field needs attention. Review its displayed default and
save before starting the addon.

**Remove** asks before stopping the addon and removing its registration and
resource access. Saved settings and private data are kept for reinstallation.
It is not a “delete all data” operation.

### If something goes wrong

Read the message at the top of Addons. **Try again** reconnects local addon
support after a failure. AJN keeps installed packages and saved settings; it does
not repeat a failed install or action for you. Review the resulting state before
trying that operation again.

If support files are missing, extract the complete preview into a new folder
and use its Manager. Replacing just the Manager executable is insufficient.
If an addon stops, read **Recent addon messages**. Expand **Technical details**
when reporting a problem to its creator.

Include the AJN preview version, addon name/version, the action you took, and
the error text. Include GPU/backend and source format for video problems. Do not
include service credentials or private media URLs in a public report.

See [Troubleshooting](#testing) for specific recovery steps.
[Creator guide](#tutorial) explains how to build an addon.


<a id="manifest"></a>

## 4. Manifest, settings, actions and saved data

The source directory contains `addon.js` and `manifest.json`. Keep a copy of
`ajn.d.ts` next to the JavaScript for editor checking. The build command wraps
your callback with the AJN SDK, compiles it and writes the package's digest.
You do not write your own JSON-RPC loop or manually generate `moduleSha256`.
The builder reads one UTF-8 `addon.js` of at most 256 KiB. Keep helper functions
in that file; extra source files are not automatically imported or bundled.

| Source manifest field | Contract |
| --- | --- |
| `schemaVersion` | Required; `1` |
| `id` | Required, permanent lowercase reverse-domain identifier; e.g. `org.example.greeting` |
| `name` | Required nonempty display name, up to 100 characters, no control characters |
| `version` | Required `major.minor.patch`, optionally a prerelease suffix; up to 80 characters; no build-metadata suffix |
| `api` | Required `{ "major": 1, "minMinor": N }`; N is the minimum API minor actually needed |
| `permissions` | Required array, including `[]` if none; only the documented permission names, no duplicates |
| `requiredCapabilities` | Optional map of up to 16 feature names to `{ "major": 1, "minMinor": N }` |
| `activation` | Optional nonempty unique list drawn from `manual`, `on_manager`, `on_player`, `on_login`; omitted means manual |
| `settings` | Optional object of up to 32 setting definitions |
| `actions` | Optional object of up to 16 action definitions |
| `moduleSha256` | Generated in the built package; do not add a placeholder to your source manifest |

IDs start with a lowercase letter and have at least two dot-separated segments.
The first segment uses lowercase letters/digits; later segments start with a
letter and may also contain hyphens. Maximum ID length is 100 characters.
Setting/action/storage keys start with an ASCII letter or digit and otherwise
use letters, digits, underscores, dots or hyphens; maximum length is 64.
Names and IDs are case-sensitive. Use your own namespace for an addon you publish.

Every setting requires `type`, `label` and `default`. Labels are nonempty, up
to 100 characters; optional descriptions are nonempty, up to 500. Neither
accepts control characters. Defaults must pass the same validation as saved values.

| Type | Values and optional constraints |
| --- | --- |
| `boolean` | JSON `true` or `false` |
| `number` | Finite JSON number; optional inclusive `minimum` and `maximum`; no integer-only schema type |
| `string` | String; optional `maxLength` from 1–4096, default 4096 |
| `choice` | String selected from required `choices`: 1–32 unique nonempty strings, each at most 100 characters |

Action definitions accept `label` and optional `description` with the same
display-text bounds. Their keys appear as `event.data.id` during an `action`
event. Arbitrary action arguments, dynamic setting schemas and addon-provided
controls are not part of the current interface. Use saved settings as action
inputs. A number setting can contain a fraction: validate/normalize it in your
addon when your application requires an integer.

`ajn.settings.get()` reads the effective settings. Only Manager or the trusted
CLI changes them; there is no `ajn.settings.set()`. On `settings.changed`, reread
the complete effective settings rather than treating event data as your database.
Use settings for user choices and `ajn.storage` for private application state.

Storage holds JSON values by key: at most 256 keys, 32 KiB per value and 1 MiB
total per addon. A missing key and a stored `null` both read as `null`. There is
no delete/list/transaction method in the public storage API. Save related state
in one value for an atomic write. In-memory variables survive events in one
worker, but disappear on restart. Do not store active handles for later reuse.

Updates, rollback and removal retain settings/private data. Resource approvals
are tied to the exact package and need separate review. Changing your addon ID
creates a different identity and does not migrate old state. Use a version field
inside persistent values and design readers to tolerate rollback; AJN executes
no addon migration/setup scripts. Unknown saved setting fields can be retained
for rollback but are hidden from the active addon. Handle incompatible saved
values explicitly instead of silently replacing a user's configuration.


<a id="recipes"></a>

## 5. Choose capabilities and permissions

Start with the [creator tutorial](#tutorial). This page helps you choose a
tested example to extend. The snippets below explain individual patterns; the
linked example directories contain complete, buildable addons.

### Pick an example

| Example source | What it demonstrates | Full guide |
| --- | --- | --- |
| [Counter](#tutorial) and [manifest](#tutorial) | Settings, private storage, lifecycle and action results | [API](#api) |
| [Session controller](#example-session-controller) | Local source/profile selections, independent sessions, status and controls | [Native media](#sessions) |
| [Sample inspector](#example-sample-inspector) | Timed binary samples from owned processing sessions | [Frames](#frames) |
| [Player inspector](#example-player-inspector) | Samples from normal playback, multiple players, independent observation handles | [Player frames](#frames) |
| [Service inspector](#example-service-inspector) | Approved HTTP requests and UDP messages, optional scoped credentials | [Networking](#network) |
| [Output inspector](#example-output-inspector) | Owned processed video/audio delivery to an approved HTTP receiver | [Outputs](#media-io) |
| [Remote inspector](#example-remote-inspector) | Remote media input, range seeking and independently approved remote output | [Remote inputs](#media-io) |

Each example has a neighboring `manifest.json`. Review its permissions and
required capabilities before copying its code. Change the package ID for your
own addon. Device vendors, codecs, stream counts and application integrations
are addon choices within the advertised platform limits.

### Optional capability checks

Capability availability and permission approval are different. This helper checks
the version; it does not grant anything:

```javascript
function supports(ajn, name, major, minimumMinor) {
    const capability = ajn.info().capabilities[name];
    return capability && capability.major === major && capability.minor >= minimumMinor;
}
```

Use `requiredCapabilities` in the manifest if the addon cannot work without a
feature. For optional behavior, check it when needed and explain what is absent.
`ajn.info().permissions.includes("player.observe")` checks the actual grant.
Still handle permission/access errors at the operation: a capability alone
does not authorize a source, profile, destination or credential.

Minimum API minors for these feature families are 0 for settings/storage,
1 for native session controls, 2 for frames/timers, 3 for networking, 4 for
outputs, 5 for remote input and 6 for normal-player observation. Independently
versioned capability requirements remain authoritative. See [API](#api).

### Timed work without blocking

Within `start`, call `ajn.timers.set("status", 250)` to request repeat events.
Within a `timer` event, check `event.data.timerId`, do a small bounded amount of
work, and return. Within `stop`, clear the timer and release owned resources.
Missed ticks coalesce; `missedTicks` describes missed intervals. Do not enqueue
one task for every interval that elapsed while the addon was busy.

Do not use `async function onEvent`, promises, sleeps or a polling `while` loop.
Native opens and HTTP requests return handles so work can continue outside the
callback. Use later timer events to observe their results. The
[sample inspector](#example-sample-inspector) is a complete timer example.

### Owned videos versus normal-player observations

Use `sessions.open` when your addon owns a separate processing session. The user
approves its input and saved profile. You can pause/seek/close your own session;
other addons cannot use its ID. These sessions consume processing capacity.

Use `playerFrames` when you only need small images from videos the user is already
playing. It requires its own `player.observe` and `frames.read` grants. It exposes
no filename, raw player command channel or playback controls, and does not open
another processing session. Multiple observers share one producer per player.

Never persist session, player, subscription or request IDs across worker restarts.
Re-enumerate players and approved resources, then create new handles. A selected
player can close between listing and subscribing; handle `player_not_found` or
`player_closed` and offer a fresh selection.

### Reading image samples correctly

`frames.read` and `playerFrames.read` return `null` when there is no new sample.
The SDK returns pixels as a `Uint8Array`, separate from JSON. For a valid sample:

```javascript
function centerRgb(frame) {
    const x = Math.floor(frame.width / 2);
    const y = Math.floor(frame.height / 2);
    const offset = y * frame.stride + x * 4;
    return [frame.pixels[offset + 2], frame.pixels[offset + 1], frame.pixels[offset]];
}
```

Pixels are BGRA8 with opaque alpha, not RGBA. Respect `color`, `crop`,
`pixelAspectRatio`, `rotation` and `verticalFlip` when interpreting geometry.
The preview provides processed SDR samples before subtitles, OSD and final
display tone mapping. Some sources/backends cannot produce samples. Report
`frame_format_unavailable` instead of treating failure as a black image.

Use `epoch` to reset temporal smoothing/history after a seek or file change.
Frame IDs and epochs are strings; do not convert them to JavaScript numbers and
lose integer precision. Slow consumers drop samples. PTS is media time, not
measured monitor display time. Start at a small size/rate and measure before
increasing them. The maximum sample is 320×180 at 60 Hz.

### HTTP and UDP

Get `network.selections()` and let the user choose an approved destination. An
empty array is a setup state, not a reason to hardcode a new address. For HTTP,
`network.request` returns a request ID; poll `network.result` on a later timer.
Reading a terminal result consumes it and frees its slot. After cancellation,
read the terminal result as well. HTTP success status and transport completion
are separate: examine the returned status code.

Service credentials are attached by trusted code only when requested and granted.
Your addon should not ask users to paste credentials into ordinary string settings
or logs. The destination review establishes an origin and pinned addresses;
changing the request path cannot broaden that authority. Redirects, arbitrary
cookies and OS credentials are not provided.

UDP send success means the datagram was sent, not acknowledged by a device.
API 1.7 provides approved HTTP(S) listeners and native WebSocket proxy tunnels,
with separate permissions and ownership. There is no unrestricted TCP/TLS socket
API. See [Networking](#network) and [HTTP/media streaming](#streaming).

### Media input and output

Remote media uses `sessions.openRemote`, not repeated HTTP reads in Wasm. The
trusted reader supplies bytes to the native pipeline and supports seeking only
when the source provides the required stable range metadata. Check session
status before seeking. Playlists and redirects are currently unsupported.

Use `outputs.formats()` before choosing codec/container/audio combinations.
Owned output sessions flow through the trusted encoder and transport without
putting continuous media bytes into JSON or Wasm. `sessions.pause`, `status` and
`requestClose` manage these handles; encoding output is not generally seekable.
Input and output services have independent approvals and credentials.

An accepted open is not successful playback or delivery. Poll loading/running/
completed/failed status, communicate failures, and close terminal sessions to
release capacity. The current native output adapter needs NVIDIA NVENC; the
supported combination matrix and resource budgets are in [Outputs](#media-io).

### Build and share responsibly

Use a small, generic diagnostic addon to establish a capability before adding a
large application integration. Test unavailable hardware and missing permissions
alongside the success path. Keep secrets out of action results and logs. Prefer
short messages for users and compact structured diagnostics for developers.

For debugging, `replay` sends recorded events to one worker; the
[counter replay file](#cli) shows its format. Replay is a
developer facility, not an authority to open resources. The broker still enforces
grants and ownership. Read [Troubleshooting](#testing) when a callback,
permission, resource selection or native operation fails.


<a id="lifecycle"></a>

## 6. Lifecycle, timers and resource ownership

AJN delivers one `start`, then serialized events such as `action`,
`settings.changed` and `timer`, and eventually `stop`. Your function receives
`{ type: "event", eventId, name, data }` and the `ajn` object. Unknown future
events/data fields should be ignored. Return a JSON-compatible value or
`undefined`; return a string or `{ message: "Readable result" }` for users.
Other JSON appears as technical diagnostics. Never return pixel arrays or a Promise.

| Activation | Behavior |
| --- | --- |
| `manual` | User chooses Start addon; work can continue after Manager closes |
| `on_manager` | A connected Manager holds an activation reason |
| `on_player` | Attached players hold activation reasons; one worker is shared |
| `on_login` | Windows sign-in activation, only after the user's explicit startup opt-in |

The same addon has one worker in a host, even if several players/Manager windows
need it. Dropping one reason does not stop other reasons. `start` is not a
per-player event. Use `playerFrames.list()` to discover attached players.
Stopping clears current reasons; a later declared activation can start the addon
again. A failure is reported, not retried indefinitely. The first addon installed
while a video is already open needs that player reopened for attachment.

Manager action buttons require a running addon. CLI `action` intentionally runs
start/action/stop in one invocation; it cannot leave an independent session alive.
Use Manager for persistent native workflows. Developer replay retains a single
worker for its sequence but does not manufacture native resource approvals.

Every callback has a two-second deadline including API calls, at most 128 broker
requests per event and 500 per second. Callbacks may fail if these limits are
exceeded. Open a request/session, remember its ID, return, and check it during
later timer callbacks. Do not spin, sleep or wait for the GPU/network in JavaScript.

`ajn.timers.set(timerId, intervalMs, repeat = true)` creates/replaces a timer.
Intervals are 16–3,600,000 ms; at most eight timers and 60 background callbacks
per second per worker. Timer data contains `timerId`, `elapsedMs`, `missedTicks`.
Missed ticks coalesce. Clear timers with `ajn.timers.clear(timerId)`. A timer is
not a display clock or a promise that the callback runs on time.

Keep session/request/subscription IDs only while the worker is alive. Source,
profile, service and player IDs must be discovered from their current selection
APIs; do not construct them or use another addon's IDs. A selected resource may
disappear between listing and use, so handle the operation's error too.

For native sessions, use `requestClose`, retain the ID while cleanup runs, and
poll status later. `closing` means cleanup is underway; `cleanup_failed` requires
a retry. `session_not_found` after successful close means the reservation is gone.
A failed/completed session still occupies capacity until closed. HTTP terminal
results are different: reading the result consumes it and releases its slot.
After `network.cancel`, continue polling until that terminal result is consumed.

On stop, request prompt cleanup and return. The host also cleans owned resources
after worker failure/exit. Do not extend the callback deadline to finish cleanup.
An unexpected uncaught error stops the worker. Catch recoverable setup/status
errors where you can offer a concrete remedy; do not hide unknown failures in an
unbounded retry loop.


<a id="sessions"></a>

## 7. Independent processing sessions

The Windows host can run independent AJN processing sessions in supervised native processes. A real Wasm addon has been tested opening two DirectML sessions, producing 960×720 frames from a 480×360 video, pausing/seeking one while the other advances, and closing them cleanly. The native null output avoids downloading GPU frames merely to discard them. API1.2 additionally offers GPU-reduced SDR samples through the separate [frames capability](#frames). API1.4 adds [owned encoded outputs](#media-io) to approved HTTP receivers, backed by the [native encoding/audio adapter](#media-io).

### Select media in Manager

Frame samples currently require DirectML/D3D11 and represent processed SDR
images. CUDA/TensorRT and final-display samples are unavailable. The owned
worker opts into `vo-null-accept-hwframes`; ordinary null-output benchmarks keep
their previous default. See [release integration](#provenance).

Use the integrated native-media preview. Install a local addon requesting `sessions.manage`, approve that permission, then use its **Processing access** section:

1. **Allow a media file**: choose one local video and review its exact path and the addon receiving access.
2. **Allow a saved profile**: choose a built-in or saved slot and backend. Manager copies the saved AJN configuration into a private snapshot; subsequent edits to player settings do not change existing approvals or sessions.
3. Start the addon and use its declared actions. The generic `session-controller` example processes the first approved source/profile and supports independent session controls.
4. **Remove access** stops the addon and drains its sessions before removing that selection. If cleanup fails, the approval is retained and the operation reports an error; retrying continues cleanup. Removing the addon also removes its media approvals and preserves ordinary settings/storage.

Approvals belong to the exact package hash, not merely the addon name. New package versions see no previous file/profile approvals. Up to 16 sources and eight profile snapshots can be approved per addon. Files are read-only. Supported local container families are MP4/MOV, Matroska/WebM, AVI and MPEG-TS. Playlists, network paths, external references and automatic sidecar loading are disabled. API1.5 provides separately approved [HTTP media sources](#media-io).

### Public contract

Declare API 1.1 and required capability `sessions: { major: 1, minMinor: 1 }`, plus permission `sessions.manage`. Optional consumers can inspect `ajn.info().capabilities.sessions`. Capability availability and user permission are separate checks. API 1.5 additionally offers [approved HTTP media sources](#media-io), with a separate input permission and capability.

```javascript
const resources = ajn.sessions.selections();
const {sessionId} = ajn.sessions.open(resources.sources[0].id, resources.profiles[0].id);
// Later events/actions:
const state = ajn.sessions.status(sessionId);
ajn.sessions.pause(sessionId, true);
ajn.sessions.seek(sessionId, 30);
ajn.sessions.requestClose(sessionId);
```

Source/profile IDs are opaque and specific to this addon package. Session IDs belong to this running addon instance. Guests cannot pass paths, native options, commands, library names or handles. They cannot use another addon's selections or sessions. Omitting a profile is allowed only when exactly one profile has been approved.

Opening returns promptly while decoding/model initialization proceeds. Cached status is refreshed about four times per second: `starting`, `opening`, `loading`, `running`, `paused`, `completed`, or `failed`. Optional fields include position/duration in seconds, seeking, input/output dimensions, pixel format, decoder, and the last native command result. Missing/unknown numeric values are null. Pause/seek are accepted asynchronously; read status on a later event to observe the result. Native errors are reported in status. A failed/completed session still owns its reservation until closed.

Use **requestClose** for native sessions. It starts cleanup without occupying the addon's two-second callback deadline. Status is `closing` during cleanup, or `cleanup_failed` if a retry is needed. After successful cleanup, the handle returns `session_not_found`. Calling requestClose again retries failed cleanup. The API 1.0 `close` method retains its original synchronous-release semantics; it is not suitable for potentially slow native shutdown. No existing method changed semantics in API 1.1.

Each addon decides how many independent sessions it needs within host limits. The default native capacity is two across all addons. Manager's **Performance limits** saves a limit from 1 to 16. Changes affect new admissions; existing and reserved sessions continue. 

Native capability discovery depends on the matching full runtime. Use the full
preview's Manager for these examples; standalone CLI tutorial invocations have
no native provider. Engines initialize on demand; no TensorRT engines are
distributed by this addon package. Initial desktop validation uses DirectML;
TensorRT/RIFE and other hardware combinations need their own tests.


<a id="frames"></a>

## 8. Image samples and normal-player observations

API 1.2 adds `frames` capability 1.0 and `timers` capability 1.0. These are additive:
older JSON-only clients keep their existing messages and session behavior.

See [initial performance measurements](#frames) for measured payload
rates, the test setup and the limits of the current performance evidence.

### Requesting samples

The addon needs `frames.read` and `sessions.manage`, and must own the processing
session. The user separately approves that addon's exact source file and profile.
Permissions apply to the installed package hash. `host.info().capabilities.frames`
is present only with a sample-capable native runtime. Capabilities do not grant
permissions, and the chosen session can still have an unsupported format/backend.

```javascript
const subscription = ajn.frames.subscribe(sessionId, {
    stage: "processed", format: "bgra8", width: 64, height: 36, maxFps: 30,
});
ajn.timers.set("samples", 33);
// In an event whose name is "timer" and data.timerId is "samples":
const frame = ajn.frames.read(subscription.subscriptionId);
if (frame) {
    // frame.pixels is a Uint8Array, not base64 or JSON-encoded pixel values.
    // Process it here; return only a small JSON result from the callback.
}
// When finished:
ajn.timers.clear("samples");
ajn.frames.unsubscribe(subscription.subscriptionId);
```

The initial producer accepts 1..320 by 1..180 pixels and 1..60 maximum samples/s,
with one subscription per session. An addon can use independent concurrent
sessions within host resource limits. The width/height stretches the visible crop
to the sample rectangle; it does not add display letterboxing. Slow consumers
receive the latest unread sample. There is no backlog and no promise that every
source frame, or the requested maximum rate, will be delivered.

The Windows implementation supports progressive mono SDR DirectML/D3D11 frames.
CUDA/TensorRT, HDR, stereo and interlaced samples are unavailable in this producer.
`frame_format_unavailable` or `feature_unavailable` lets an addon report that
limitation or choose another supported operation. Do not reinterpret HDR as SDR.

Each BGRA8 sample carries width, height, tightly packed stride, processing PTS,
producer-clock milliseconds, source dimensions, crop, pixel aspect, rotation,
vertical flip, primaries, transfer, full RGB range and opaque alpha. Rotation and
vertical flip describe display transforms still to apply. Primaries and transfer
are preserved; matrix/range conversion alone does not make every frame sRGB.
`frameId` and `epoch` are decimal strings, avoiding integer precision loss in JS.
The producer clock is local to that native session, not a shared wall clock.

This is the processed filter image, before final display tone mapping, color
management, subtitles and OSD. Its PTS is not a measured presentation timestamp.
Future final-display and HDR sampling require explicit negotiated representations.

Seeks invalidate samples from the old filter epoch. Native seek commands remain
asynchronous; session status reports their progress. Unsubscribe turns sampling
off. Closing a session, stopping/failing an addon or revoking its media access
releases its subscriptions. A bad subscriber never owns GPU textures or blocks
the original video image from continuing through the pipeline.


### Observing ordinary playback

API1.6 adds optional `playerFrames` capability1.0. An addon can observe small
images from the user's ordinary mpv/mpv.net playback without opening its own
processing session. This is a framework capability; the player inspector is a
generic diagnostic example, not a lighting integration.

### Consent and API

Both `player.observe` and `frames.read` must be requested and explicitly granted
to the exact package hash. The Manager review describes observation as **Read
small image samples from videos played in AJN**. Owned-session grants do not
implicitly permit it. `sessions.manage` is not required and no pause/seek/open
controls are granted. Network access remains separately permissioned.

| Method | Result |
| --- | --- |
| `playerFrames.list()` | Attached players as opaque `playerId`, `stage`, `format` records; no title, filename or process identifier |
| `playerFrames.subscribe(playerId, options)` | Owned subscription ID and accepted `stage`, `format`, `width`, `height`, `maxFps` |
| `playerFrames.read(subscriptionId)` | Latest unread `AjnFrame` with bounded binary pixels, or `null` |
| `playerFrames.unsubscribe(subscriptionId)` | Releases this observer; other addons and playback continue |

Sample options and metadata match [FRAMES.md](#frames): BGRA8, processed SDR,
1..320 by1..180 pixels,1..60 Hz; source geometry, crop, rotation, pixel aspect,
color metadata, media PTS and seek/file epochs are retained. This point precedes
final display tone mapping, subtitles and OSD. `ptsSeconds` is media processing
time, not a measurement of when a monitor displayed the image.

Each player has one producer. Its negotiated width, height and rate are the
maximum of current subscriptions. The host reduces that small image to each
reader's dimensions with bilinear interpolation in the encoded SDR values.
Readers keep independent frame IDs and rate limits; an unread sample is replaced,
never queued. The host copies only these bounded small images into Wasm.

The preview admits four attached players, eight readers per player and four per
addon instance. Existing worker binary/control quotas also apply. These are
resource ceilings; an addon chooses its own usage within them. Owned processing
sessions retain their independent user-configurable capacity.

Closing an addon releases its subscriptions. Closing a player invalidates its
subscriptions with `player_closed`; unsubscribe still releases the handle.
Unsupported formats report `frame_format_unavailable`; a native readback failure
reports `frame_unavailable`. A lease with no fresh producer can return `null`.
Do not interpret it as a black frame. Use host timers for future reads.

### Try the inspector

1. Use a full preview containing the matching native observation adapter.
2. Install `player-inspector.ajnaddon` and review its two unchecked permissions.
3. Open a video in AJN. Start the inspector and choose **List attached players**.
4. Choose a player number, sample dimensions and rate in settings, then choose
   **Observe selected player** and **Show latest sample**.
5. **Stop observing** removes that subscription. Stopping the addon does the same.

The inspector sends no network traffic. Manager actions require a running addon; keep the inspector running
between subscribe and sample actions.
An already-open player sees its first installed addon on its next launch.


<a id="network"></a>

## 9. HTTP, UDP and credentials

API 1.3 adds `network` capability 1.0 and Windows `credentials` capability 1.0.

API 1.4 can also send encoded media to an approved HTTP/HTTPS service through
the separate `outputs` capability and `media.output` permission. Manager
discloses media delivery when that permission is granted. These outputs use
their own streaming limits; ordinary HTTP/UDP calls below retain their existing
bounds. See [OUTPUTS.md](#media-io).
Wasm still has no socket, DNS or HTTP imports. `network.connect` alone approves
no destination.

### Consent

Manager accepts an HTTP/HTTPS origin or UDP hostname/IP and port. The host
resolves it during review and shows its canonical origin, protocol and IPs.
A three-minute, single-use review reference and package hash bind final approval
to what was shown. Cancelling grants nothing. Maximum: eight destinations per
package, each with at most eight unicast IPs.

Approval covers all paths at the HTTP origin, or datagrams to the UDP port.
Guests supply opaque IDs, never replacement hostnames, schemes or ports.
Connections use only the pinned IPs, without another DNS lookup. If a service
changes addresses, remove its old approval and review again; this also applies
to CDN and local-device changes. Loopback/private services need the same review.
Wildcards, broadcast, multicast and unspecified addresses are rejected.

Approvals belong to an exact package hash. Removing access stops the addon and
drains its work before committing removal. Failed cleanup retains approval for
retry. Removing an addon clears its destinations and credentials; ordinary
settings/data remain separate.

### HTTP

```javascript
const service = ajn.network.selections().destinations[0];
const request = ajn.network.request(service.id, {
    method: "GET", path: "/status", useCredential: false,
});
// Poll on a timer or later action, without spinning inside a callback.
const response = ajn.network.result(request.requestId);
// response.state: pending, completed, or failed.
// Completed: status, headers, body (Uint8Array). Failed: structured error.
```

Supported methods: GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS. Paths start with
a single slash. Host/connection/framing/proxy headers are host-controlled.
Bodies are at most 32 KiB, empty for GET/HEAD. The SDK accepts Uint8Array or UTF-8
strings, encoded as wire `bodyBase64`.

The adapter uses HTTP/1.1 and pinned TCP connections, with normal HTTPS hostname
and certificate verification. It does not inherit proxies, OS credentials or
browser cookies. Automatic redirects, cookie storage and decompression are off.
Redirects return a 3xx response and cannot broaden access or carry a saved header
to another service. Requests are never automatically retried. Cancellation
cannot undo work already received by the server.

Four retained request slots belong to each instance; sixteen requests can be
active across the host. Each has a fifteen-second overall deadline and five-
second connection deadline. Slow requests run outside the addon event gate.
Starting requests is limited to 32 calls and 1 MiB of bodies/s per addon.

A completed result includes HTTP status (including HTTP error statuses), bounded
headers and at most 64 KiB of body. Transport failures have a structured error.
Terminal results are consumed once and free their local slots; later reads get
`request_not_found`. `cancel(requestId)` requests cancellation; consume its
terminal result to free that slot. Owner shutdown cancels/drains all requests.

Only `network.result` adds binary framing: its JSON declares `byteLength`, then
exactly that many bytes follow the newline. Pending/failed results declare zero.
The SDK exposes the tail as `body`. Existing frame framing and API 1.0/1.1 calls
are unchanged. AJN reserves room before consuming an HTTP result. A full worker
binary budget returns `bandwidth_exceeded`, leaving it readable later. Reduce
competing sample traffic before retrying; the shared budget is 16 MiB/s.

### UDP

`ajn.network.sendDatagram(destinationId, bytes)` sends one nonempty datagram,
at most 16 KiB, to the first pinned address for that UDP destination. Success
reports OS bytes sent, not receipt/application by the device. Select a direct
IP when ordering matters. Use the device protocol's sizes and sequencing.
There is no receive, retransmission, reliable delivery or reassembly API.

Limits: 240 datagrams and 1 MiB/s per addon; 960 datagrams and 4 MiB/s across the
host. At most four sends per owner can be pending, each with a 250 ms deadline.
Rate-limit errors are recoverable after reducing traffic.

### Saved headers

An addon also needs `credentials.use`. The user selects an approved HTTP service
and saves a header name plus complete value in Manager's masked field. The value
is never returned by lists or a guest credential-read API. Windows user DPAPI
encrypts it with context bound to the addon ID, package hash and destination ID.
Replacing/removing a credential stops the addon; the destination can remain.

`useCredential: true` attaches the saved header only to its approved service.
The guest cannot override that same header. Without the flag, it is absent.
The trusted host necessarily holds plaintext while sending it. The service
receives it and could echo it back: scoped use does not guarantee a cooperating
server cannot reveal a value. Manager explains unencrypted HTTP transmission.
No unrelated Windows/browser credential store is exposed. OAuth, browser login,
token refresh and Linux credential providers remain work.


<a id="media-io"></a>

## 10. Remote media and encoded output

### Remote input

A Wasm addon can process an HTTP/HTTPS media resource from a service the user
approved in Manager. It can also connect that input to an approved encoded-output
receiver. The API supports independent concurrent sessions within the configured
host capacity. It does not implement a Plex addon or impose a Plex session policy.

Declare `remoteSources: { major: 1, minMinor: 0 }` in `requiredCapabilities`, or
discover it through `ajn.info().capabilities`. Declare `media.input`,
`sessions.manage` and `network.connect`; the user grants each independently.
Saved source headers additionally require `credentials.use`. Manager explains
that source approval covers media at all paths on that approved service.

Approve a service and processing profile first; no local-file grant is needed.
Obtain IDs from `ajn.network.selections()` and `ajn.sessions.selections()`.

```js
const source = {
    type: "http",
    destinationId: approvedSourceService.id,
    path: "/media/example.mkv",
    useCredential: false,
};
const { sessionId } = ajn.sessions.openRemote(source, approvedProfile.id);
// Observe initialization using sessions.status. No network body enters Wasm.
```

`ajn.outputs.openRemote(source, profileId, outputOptions)` uses the same encoding
and destination options as [outputs.open](#media-io), with a separate
`media.output` grant. Input and output credentials are selected independently;
the host never sends one service's credential to the other. Short output duration
ends the input intentionally. Seeking an encoded output requires a new session.

The [remote inspector](#example-remote-inspector) demonstrates processing,
pause/seek/close, capacity, service selection, and optional H.264/AAC upload. Its
startup is idle. The user explicitly invokes a processing or upload action. Its
session count is an example setting, bounded by host admission.

### Reading and seeking

Only a single Matroska/WebM, MOV/MP4, AVI or MPEG-TS resource is supported. No
HLS/DASH/playlist traversal, redirects, automatic refresh, or reconnect/resume is
implemented. The same approved IPs and origin apply to every GET. Normal HTTPS
certificate checks remain active. Browser cookies, proxy settings and operating
system credentials are not inherited. Responses must use identity encoding.

The reader initially requests `Range: bytes=0-`. A full `200` response works as
a forward-only stream, including chunked responses with no known length. `206`
responses must have consistent, bounded Content-Range and Content-Length values.
Seekable streams need a strong ETag, or no ETag and an eligible Last-Modified
date. The date must be at least 60 seconds older than the response Date; this is
the preview's conservative eligibility policy. Weak ETags never use the date
fallback. Subsequent GETs send If-Range; changed validators, changed lengths or a
server ignoring the condition fail the session instead of joining different
versions. See the [HTTP conditional-range specification](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.5).

`sessions.status` adds `input: { type, seekable, bytesRead, requests, errorCode }`.
Check `input.seekable` before seeking. `bytesRead` counts received bytes, including
rereads after seeking, rather than a download percentage. EOF completes normally;
incomplete bodies fail with `input_truncated`. Other codes distinguish response,
range/representation changes, unsupported encoding, deadlines and limits. Public
status never includes a source credential or its media path.


### Input bounds



`ajn.remoteSources.formats()` advertises the supported formats and these limits:

| Bound | Preview value |
| --- | --- |
| Transfer and declared media size | 512 GiB per input |
| Read buffer | 64 KiB |
| Transfer rate | 8 MiB/s per input, plus one read-buffer burst |
| HTTP response headers / connection | 16 KiB / 5-second connect deadline |
| Header or body I/O deadline | 10 seconds |
| Range requests | At most 32/s and 65,536 per input |
| Lifetime | 24 hours per input |
| Concurrent native sessions | User-configured 1–16, default two, shared with local/output sessions |

There is no separate global input bandwidth limiter yet; aggregate throughput is
bounded by the configured session capacity. Small control HTTP requests retain
their separate network API limits. File metadata can require more than one
range request during probing; the reader does not promise one request per video.


### Encoded output

`outputs` capability 1.0 connects an owned AJN processing session to a separately
approved receiver. The first adapter sends a chunked HTTP/1.1 POST or PUT. It
does not implement Plex, a web server, discovery or arbitrary native plugins.
Windows is implemented first; public messages contain no OS handles or paths.

### Permissions and resources

Opening an output requires **`media.output`**, **`sessions.manage`**, and
**`network.connect`**. These are independent of capability discovery. The user
must separately approve the source file, saved profile snapshot and destination
for this exact addon package. Manager explains that an output-enabled addon can
send processed video and audio to the reviewed service. Updates do not silently
inherit an old package's resource or permission grants.

Only HTTP/HTTPS destinations are accepted. The host connects to their reviewed
IP addresses and retains normal HTTPS certificate/hostname validation. It does
not follow redirects or inherit browser cookies, system proxy settings or OS
credentials. Optional saved request-header credentials require `credentials.use`
and `useCredential: true`; the value never enters the guest response. Revoking
media/network access or changing a credential stops the addon and drains its
outputs before changing the grant. See [NETWORK.md](#network).

### SDK

```javascript
const resources = ajn.sessions.selections();
const receivers = ajn.network.selections().destinations;
const formats = ajn.outputs.formats();
const { sessionId } = ajn.outputs.open(resources.sources[0].id, resources.profiles[0].id, {
    encoding: {
        videoCodec: "h264", container: "matroska", videoKbps: 4000,
        audioCodec: "aac", audioKbps: 128, keyframeFrames: 60, lengthSeconds: 2
    },
    destination: {
        type: "httpUpload", destinationId: receivers[0].id,
        path: "/media", method: "POST", useCredential: false
    }
});
// In a later action or host timer callback:
const state = ajn.sessions.status(sessionId);
// Retain the ID until cleanup has finished; it then returns session_not_found.
ajn.sessions.requestClose(sessionId);
```

Choose actual approved selections; the example's `[0]` indexes are illustrative.
The [output inspector](#example-output-inspector) exposes selection and
format settings, multiple sessions, status, pause/resume and close actions. It
sends nothing on startup. **Start** it before choosing **Send processed media**;
Manager actions require the addon to be running.
Use a receiver you control that accepts streaming request bodies. Merely running
a normal file-serving HTTP server does not provide an upload endpoint.

`outputs.formats` requires `media.output` and `sessions.manage`; it describes
this adapter's options, bounds and host session limit. It is not a GPU/driver or
remote-client compatibility probe. This preview uses NVIDIA NVENC even when AJN
inference uses DirectML. Unsupported hardware or input formats produce a failed
session. Other encoders/transports can be added through capability negotiation.

| Choice | Current adapter |
| --- | --- |
| Video | H.264 (`h264`), HEVC (`hevc`), AV1 (`av1`) |
| Container | Matroska (`matroska`), MPEG-TS (`mpegts`), fragmented MP4 (`fragmentedMp4`) |
| Audio | Disabled (`none`, SDK default), AAC (`aac`), Opus (`opus`) |
| MPEG-TS combinations | H.264 or HEVC; audio disabled or AAC |
| Bitrate | Video 256–50,000 kbps; audio 32–512 kbps, default 128 |
| Keyframe interval | 1–600 frames, default 60 |
| Duration | 0 for the source's remaining duration, or up to 86,400 seconds |
| Receiver | Approved HTTP/HTTPS origin, relative path, POST or PUT |

Without playback options, encoding starts at the beginning of the selected source.
API 1.7 adds explicit start offsets, audio/subtitle choices, and a `servedStream`
destination; see [STREAMING.md](#streaming) for the complete contract. Seeking
requires closing and reopening output; pause/resume retains the current timeline.
The receiver sees one muxed video stream and optional selected audio track.
Subtitle burn-in uses the separately selected software composition path.
Encoding proceeds at the producer's rate
with transport backpressure; this is not a guarantee of realtime pacing or a
particular client latency. Format/color limitations and native details are in
[NATIVE-OUTPUT.md](#media-io).

### Ownership, limits and completion

Each open returns promptly with an owned `sessionId`. Use the existing session
API for status, pause and asynchronous close. Output and non-output sessions
share admission; the default native limit is two, configurable by the host up
to sixteen. Addons choose how many to request within that capacity. A completed
or failed handle retains its admission slot until explicitly closed or its
owner stops. Failed cleanup retains ownership/capacity and can be retried.

Manager's **Performance limits** changes the shared native-session limit. Lowering it
preserves active outputs and prevents new admission until enough handles close.

Full-resolution frames and continuous encoded bytes stay in trusted components:
native GPU processing/encoding → private bounded pipe → HTTP transport. They do
not cross guest JSON-RPC. Optional `frames.read` sampling remains a distinct,
permission-checked, reduced-image path.

The transport uses a 64 KiB copy buffer, with native codec/muxer memory covered
by the existing process/job limits. It permits 8 MiB per one-second window per
output and 32 MiB per host window; those are throughput budgets with bursts,
not smooth constant-rate pacing. A slow receiver backpressures its own producer.
Limits are 512 GiB per output and 24 hours of wall time, including pauses. Each
network write has a 15-second deadline; after the last encoded byte, the final
HTTP response has 15 seconds. Native termination gets three seconds of grace
before its job is terminated. Cleanup waits for the pending pipe read too.

Status retains native dimensions, codec/pixel-format and processing information,
plus `output: { type, destinationId, method, encoding, bytesSent, httpStatus }`.
`bytesSent` counts bytes accepted by the host's HTTP content stream; it is not a
remote durable-storage receipt. `opening`/`running`/`finishing` precede a terminal
`completed` or `failed`. `completed` requires native completion, complete upload,
a final successful HTTP status and producer cleanup. Early success, rejection,
redirects, transport failure and native failure cannot be reported as complete.
Errors include `output_rejected`, `output_incomplete`, `output_unavailable`,
`output_cancelled`, `native_output_failed` and retryable `cleanup_pending`.


### Hardware and format qualification

DirectML inference support does not imply NVENC encoder support. The validated
native output path uses Windows 11, DirectML/D3D11 and an NVIDIA RTX 5090.
H.264, HEVC and AV1 with audio were decoded and inspected in short synthetic
receiver tests. This is not a certification of every GPU, long-stream A/V sync,
end-to-end client latency, VFR, CUDA/TensorRT, RIFE or HDR. API 1.7 adds a separately
selected subtitle composition path. Rotation/interlacing and advanced color/metadata cases need
their own supported-format tests. Addon creators must state the hardware and
formats they actually tested.

Normal-player image observation does not supply an encoded copy of that player's
stream. Output APIs create independent owned sessions from approved sources.
They cannot attach an encoder to another player's existing session. An HTTP
upload receiver must already exist and accept streaming request bodies; the
servedStream destination instead uses approved AJN HTTP listeners. Check these constraints before
promising compatibility with a particular media service or client.


<a id="streaming"></a>

## 10a. HTTP serving, client credentials and native stream delivery

This extension lets one installed `.ajnaddon` accept approved HTTP(S) requests,
forward traffic to approved services, probe media, and serve processed audio/video.
The addon supplies service protocol and client authorization logic. AJN supplies
the HTTP transport, media worker, codecs, muxer, and temporary storage. No companion
executable is required. This is a framework API, not a Plex integration.

Use the matching runtime, SDK and developer tools. Native format qualification is
recorded separately in the release's test evidence; format enumeration does not
prove that a GPU/driver/profile can encode in real time. API 1.7 is a preview.

### Capabilities and permission review

Set `api: { major: 1, minMinor: 7 }` and declare each required capability as
`{ major: 1, minMinor: 0 }`. An older host rejects an unavailable required capability
before running the addon. Optional features can be tested through `ajn.info().capabilities`.
Never assume that a newer host contains every optional native component.

| Capability | Required access |
| --- | --- |
| `httpServer` | `network.listen` plus a reviewed listener |
| `httpProxy` | `network.proxy`, `network.connect`, reviewed upstream; forwarding also needs `network.listen` |
| `requestCredentials` | `credentials.delegate`, `credentials.use`, `network.connect`; capture uses an owned listener request |
| `mediaProbe` | `sessions.manage`, `media.input`, reviewed source; remote input also needs `network.connect` and upstream review |
| `mediaStreams` | `sessions.manage`, `media.input`, `media.output`, reviewed source/profile; remote input also needs network access |
| `outputPlayback` | `sessions.manage`, `media.output`, matching native playback runtime; source/output grants still apply |
| `subtitles` | `sessions.manage`, `media.input`, reviewed subtitle source or embedded track; remote input needs network access |

Serving stream or subtitle resources also requires `network.listen`. Saved
upstream credentials require `credentials.use`; request-derived contexts require
both credential permissions. Source, profile, listener, certificate and upstream
approvals are independent and bound to the exact reviewed package hash.

Existing HTTP uploads retain their defaults, including `audioCodec: "none"`.
Existing non-output `sessions.seek` keeps its original meaning. API 1.0–1.6 addons
do not acquire new access when AJN is updated.

### User setup in Manager

1. Install the `.ajnaddon`, review its permissions, and select it in **Addons**.
2. Approve its media source and processing profile. For remote input, approve the
   upstream service in networking access instead of supplying a local video path.
3. In **Listening access**, add a listener. The default accepts only connections
   from this PC. Access from other computers requires an explicit LAN/public
   choice, bind address, port, and accepted host names.
4. For HTTPS, use **Import HTTPS certificate** to import a PFX/PKCS#12 file with its
   private key, then select it and its covered hostname for the listener. AJN
   checks validity, server identity and expiry and protects the key for this Windows user.
5. Start the addon and use its action to open the listener. Approval alone does not
   bind the port. If binding fails, inspect the addon status for its port/certificate error.

Certificate replacement stops the addon and its listeners. Restart the addon to
use the replacement. Importing a certificate does not make clients trust it.
Certificates, DNS and routing must match the address clients actually use.

The optional public base URL is configuration only: `reachability` is `unverified`.
AJN does not open a firewall, forward router ports, supply public DNS, overcome
CGNAT, or route an existing client through an addon automatically. There is no
automatic public reachability test in this preview.

Review CORS explicitly if a browser client needs it. Use exact allowed origins;
credentialed wildcard CORS is not supported. CORS controls browser access and
does not authenticate native clients. Sensitive header/query names are reviewed
with the listener and redacted before request metadata reaches the guest.

### HTTP request lifecycle

`ajn.httpServer.selections()` returns opaque approved listener IDs and non-secret
binding metadata. `open(listenerId)` returns `{ serverId }` promptly. Poll
`status(serverId)` on a timer until `listening`, or handle `failed` and close it.

An `http.request` event contains `requestId`, `serverId`, `method`, `path`, repeated
`query` and `headers` entries, peer address, and WebSocket-upgrade information.
Repeated fields use `{ name, values: string[] }`, not a flattening dictionary.
Returning from the event does not complete its request. Store only the opaque
request ID and the small state needed for a later callback.

Authorize every protected request, then choose **one** terminal claim:

- `httpServer.respond(requestId, { status, headers, body })` for up to 32 KiB.
- `beginResponse`, `appendResponse`, `finishResponse` for a buffered response up
  to 256 KiB, appended in at most 32 KiB chunks across short callbacks.
- `httpProxy.forward` for native upstream forwarding or a WebSocket tunnel.
- `mediaStreams.serve` for processed media, or `subtitles.serve` for WebVTT.

The host owns HTTP framing and hop-by-hop headers. Do not supply `Host`,
`Content-Length`, transfer framing, CORS, or WebSocket-handshake headers yourself.
HEAD suppresses the body while preserving appropriate representation metadata.

For control request bodies, call `readBody(requestId)`, return from the callback,
then poll `requestStatus`. Once `bodyState` is `ready`, use `bodyChunk(id, offset,
count)`; each result has `body: Uint8Array`, `offset`, `totalBytes`, and `eof`.
Reading a body does not claim the eventual response. A proxy can stream larger
bodies natively; do not first consume those bodies into the guest.

Unclaimed requests expire after 15 seconds. `extend(requestId, seconds)` extends
from now but never beyond 120 seconds after arrival. Extend before model startup
only when you intend to retain that request; an expired request cannot be revived.
`cancelRequest` cancels only that request. Handle `request_not_found` and
`request_claimed` when a client disconnects or a terminal claim already won.

`http.closed` has `kind: "request"` and a `requestId` when an individual request
finishes. Listener closure has `kind: "listener"` and no `requestId`.
`http.disconnected` describes a canceled/disconnected request. These are lifecycle
hints: one disconnected segment request must not automatically kill playback.

`requestClose(serverId)` starts listener cleanup. Clear your saved server ID when
requesting close; after cleanup the handle no longer resolves. Stop callbacks
must not try to close an already-removed listener. See `examples/http-inspector`
and `examples/media-stream` for complete event-driven implementations.

HTTP limits: four listeners per addon, 16 per host, 128 connections and 16 upgraded
WebSockets per listener, 64 unclaimed and 128 active requests per addon, 64 KiB
request headers, 8 KiB request line, 256 KiB buffered request/response bodies.
The normal two-second callback and broker rate budgets still apply. Use short
timer callbacks to advance work; never busy-wait for a network or media operation.

### Native proxy and buffered metadata

`ajn.httpProxy.forward(requestId, destinationId, options)` claims the request and
returns `{ operationId }`. `options.path` is relative to the approved upstream
authority. Request/response bodies flow natively, including large media bodies,
HEAD, ranges, compressed payload bytes, and bidirectional WebSocket messages.

Options include `requestHeaders` and `responseHeaders` arrays of
`{ name, values }`; `values: null` removes a field. Repeated end-to-end headers are
preserved. Explicit `passRequestHeaders`/`passResponseHeaders` allow otherwise
excluded sensitive fields for this request. There is no shared cookie jar,
inherited account state or automatic redirect following. Authority/SNI, pinned
addresses, TLS verification, framing and forbidden headers remain host-controlled.

`buffer(destinationId, { path, method, body, maximumBytes, ...options })` performs
an asynchronous bounded upstream request without an incoming request claim.
Poll `status(operationId)`. Only after `completed` may `read` return the entire
body in chunks of at most 32 KiB. Maximum result size is 2 MiB; exceeding the cap
fails with `response_too_large` instead of exposing truncated success.
The request body is at most 32 KiB. Metadata transformations remain addon logic.

Poll the operation's structured status rather than assuming HTTP 2xx:
an upstream 401/403/redirect is an HTTP response, not successful authorization.
`cancel` requests cleanup. Once `cleanupReady` is true, `close` releases the
operation slot. Completed results remain owned until closed.

Limits: 16 retained operations per addon (at most four buffered), 64 per host;
15-second header and I/O deadlines; 24-hour absolute lifetime; 512 GiB native
transfer limit; 16 MiB WebSocket message limit with 32 KiB transport chunks.
Cancellation propagates through client disconnect, credential release and owner
shutdown. This capability is not a general TCP socket API.

### Request-derived credentials

For a client-authenticated integration, capture only the declared sensitive
fields from its owned pending request:

Declare custom credential fields in the manifest, for example
`"sensitiveRequestFields": { "headers": ["X-Client-Key"], "query": [] }`.
Built-in sensitive headers such as Authorization are already hidden. Declaration
does not grant capture or upstream access; the credential permissions still apply.

```javascript
const context = ajn.requestCredentials.capture(requestId, destinationId, [
    { from: "header", name: "Authorization", to: "header", target: "Authorization" }
], 3600);
const check = ajn.httpProxy.buffer(destinationId, {
    path: "/account", credentialId: context.credentialId
});
```

These variables belong to your event-driven session state. Return, poll `check`
on a later timer, and validate the upstream status/body before serving protected
content. Capture does **not** authenticate the client. AJN always reports
`authenticated: false`; the addon owns authorization and session policy.

Attach the context as `credentialId` on proxy options or a remote source descriptor
used for probing, playback, or external subtitles. It belongs to one addon
instance and one approved destination. Another destination requires its own
explicitly captured context. `useCredential: true` and a context cannot be chosen
together. A header/query field must have one credential source.

`status` exposes presence, placement and expiry, never values. `release` revokes
the context and cancels its active native operations and retained media delivery.
Do not release while a playback cache is intentionally still available. Up to 64
contexts, eight fields and 16 KiB captured values per context, at most 24 hours.
Keep separate context/session/generation mappings for different clients.

### Probe and track selection

A source is `{ type: "local", sourceId }` or
`{ type: "http", destinationId, path, useCredential?, credentialId? }`.
Call `ajn.mediaProbe.open(source)`, retain its `probeId`, poll `status`, and call
`result` only after completion. The SDK assembles the result from at most eight
32 KiB chunks. `read` provides the same chunks for custom assembly. Cancel active
work, wait for terminal status, then `close` to free the retained slot.

The probe supplies representation identity, container, duration/start,
seekability, chapters, video geometry/aspect/rotation/color/HDR/interlacing,
audio/subtitle tracks and attachment metadata. Track IDs belong to that source
representation. Reprobe after replacement; stale track IDs fail with `stale_track`.
Playback probes again before resolving explicit track choices.

Unknown values are null. Container average/nominal frame rates do not establish
CFR or VFR; `variableFrameRate: null` is deliberate. Local representation identity
uses file metadata plus bounded first/last fingerprints. Remote identity uses
the approved resource and stable HTTP validators when available. A validator-free
remote resource cannot promise stable explicit track IDs across separate opens.

Probe limits: two retained jobs per addon, eight active host jobs, 30 seconds,
64 MiB native probe-read budget, and 256 KiB result. Probing does not allocate an
encoder or upscale frames. Remote sources retain the existing validated-range,
pinned-address and no-redirect behavior.

### Open a served output

```javascript
const output = ajn.mediaStreams.open(source, profileId, {
    encoding: {
        videoCodec: "h264", container: "fragmentedMp4", videoKbps: 4000,
        audioCodec: "aac", audioKbps: 128, audioChannels: 2
    },
    playback: { startSeconds: 120, audioTrack: "default", subtitles: { mode: "none" } },
    mode: "segments", segmentSeconds: 1
});
// output: { sessionId, streamId, generationId }; no filename or public URL.
```

`outputs.open`/`openRemote` also accept a destination
`{ type: "servedStream", mode: "segments", segmentSeconds: 1 }` and return the
same handle. The legacy `httpUpload` destination remains supported.

All served output requires an approved DirectML profile and matching native
runtime. The initial baseline is progressive SDR with a supported NVIDIA encoder.
Known HDR/interlaced input is rejected rather than silently misrepresented.
`audioChannels: 2` explicitly requests stereo downmix. Audio codec omission still
means no encoded audio; `audioTrack: "none"` disables it even if a codec is chosen.

`startSeconds` is nonnegative, finite and absolute in the source timeline. Nonzero
offsets require seekable input; offsets at/beyond known duration fail.
`encoding.lengthSeconds` is duration after the chosen offset, or zero for EOF.
Audio selection is `"default"`, `"none"`, or `{ trackId }` from the current probe.

Delivery choices are continuous or segmented Matroska, MPEG-TS, or fragmented
MP4. Segment target duration is 0.5–6 seconds, default one second; actual durations
follow encoded keyframes and must be read from descriptors. Fragmented MP4 uses
an initialization object. Matroska chunks are not universally compatible HLS.
AJN exposes media objects; your addon creates manifests/routes for its client.

### Serve resources and manage production

Poll `mediaStreams.status(streamId)` and `segments(streamId, cursor, limit)`.
Pages contain up to 32 immutable segment descriptors, an optional initialization
ID, a continuous-resource ID when applicable, and a generation-bound next cursor.
An empty page means no newly published object yet, not necessarily EOF.

Each segment reports its sequence, source start/end, actual duration, encoded
timestamp origin, byte length, ETag, independence, and initialization reference.
Use `encodedTimestampOriginSeconds` when mapping encoded timestamps to the source
timeline. It can be null if the container cannot establish it. Never infer a
fixed frame rate or regenerate segment timing from a guessed FPS.

After authenticating and authorizing a request for this playback session:

```javascript
ajn.mediaStreams.serve(requestId, output.streamId, output.generationId, resourceId);
```

This claims the request and writes bytes natively. Resource/generation IDs isolate
ownership but are not client authentication. Initialization objects use the same
serve operation. One client must not receive another client's stream merely by
knowing its route. Reject unknown logical sessions before calling AJN.

Completed objects support GET/HEAD, ETag conditions and a single byte range
(closed, open-ended or suffix). Unsatisfiable/multipart ranges receive 416.
Growing continuous responses support sequential GET/HEAD, not ranges or seeking.
Late continuous readers begin at the retained beginning, not an arbitrary current
byte position. Use a replacement stream to begin at a new time.

Call `setDemand(streamId, absoluteSourceSeconds)` from actual client playback
progress. Downloading a segment is not proof that the client viewed it. Production
pauses when the next packet would exceed 15 seconds ahead and resumes when
updated demand lets that packet fit within the window. `pause(id,
true)` is a separate user pause; `pause(id, false)` resumes the same generation.
Periodic demand/pause ownership updates are needed during a long intentional pause.

Limits: four retained caches per addon and host, 2 GiB each (8 GiB reserved total),
16 readers per stream, two-minute segment-reader lifetime, 15-second I/O deadline,
24-hour absolute lifetime. Segments more than 30 seconds behind demand expire
only when no reader holds them. Continuous output retains its beginning and can
reach the 2 GiB cap. Storage pressure pauses writes for at most 15 seconds while
eligible objects are reclaimed, then fails with `buffer_limit_reached`.

No requests/demand updates for ten minutes expires the stream. A deliberately
paused stream can last two hours with ownership updates. The absolute 24-hour
limit still applies. Client disconnect cancels its response, not every response
or the whole logical playback. Your addon decides when playback is abandoned.

`producerCompleted` means encoding finished and native capacity was released;
cached objects remain available. `requestClose` is asynchronous and idempotent for
the retained stream handle. Wait for `closed`/`failed` and
`nativeCapacityReleased` before replacing it. `cleanupFailed` retains ownership
and capacity that could not be cleaned; retry close instead of starting an
unbounded replacement loop.

Status separates requested encoding (`native.encoding`) from observed output.
`encodedMedia` is measured from a completed encoded object, null before one is
inspected; continuous output supplies it at EOF. It includes actual codecs,
dimensions, pixel format, aspects and audio layout. The native status also gives
live pipeline observations. `measurements` reports measured media/wall speed and
effective bitrate with their intervals; unavailable values remain null.

`transfer.bytesProduced` counts published media objects (or growing continuous
bytes); `bytesServed` counts bytes actually read for responses, including repeat
reads. `cachedBytes` counts retained published media plus growing continuous
bytes. It excludes unfinished segments and metadata/index overhead, so it is not
a measurement of the directory's total disk allocation. The native quota still
covers all output files. `activeReaders` and `maximumCacheBytes` expose the current
reader count and per-stream storage limit.

### Seek, resume and track replacement

Retain your own logical playback ID. Cancel its pending old response requests,
request stream close, and poll until cleanup/capacity release finishes. Then call
open with the desired offset/tracks and replace the saved handle. This works with
one native session slot. A new stream always receives a new generation ID.

Inside-cache replay can serve retained objects, but `setDemand` cannot recreate
expired or unproduced media. An outside-range position produces
`stream_replacement_required`. For an actual seek, close/reopen works consistently
inside or outside the cache. `sessions.seek` rejects encoded streams. Ordinary
pause/resume keeps the generation. Reject requests carrying the old generation.

### Subtitles

Burn-in is selected in `playback.subtitles`:

```javascript
{ mode: "none" }
{ mode: "burn", trackId: selectedSubtitleTrackId }
{ mode: "burn", externalSourceId: approvedSubtitleSourceId }
{ mode: "burn", externalRemoteSource: { destinationId, path: "/subtitle", credentialId } }
```

Choose exactly one source. Embedded text/ASS/SSA and supported bitmap subtitles
are composed after processing and before encoding. Software composition requires
GPU download/upload and has a separate performance cost. Embedded font resources
remain native. Limits are 64 attachments, 16 MiB per attachment, 32 MiB combined.
External local/remote SRT, ASS/SSA and WebVTT require their own approval; remote
downloads have a 30-second/16 MiB bound and a separate credential scope. AJN does
not automatically search the filesystem or arbitrary subtitle URLs.

For text delivery, call `ajn.subtitles.open(source, { trackId, startSeconds,
endSeconds, allowStylingLoss })`, poll status, and serve the resulting WebVTT with
`subtitles.serve(requestId, subtitleId)` after request authorization. Track ID can
be omitted only for a standalone source containing one subtitle track and no
video/audio. Supported text codecs are ASS/SSA, SubRip, WebVTT, MOV text and text.
ASS/SSA conversion requires `allowStylingLoss: true`; positioning, fonts/drawings
are not preserved by plain WebVTT conversion. Use burn-in when those matter.
Bitmap extraction/OCR is unavailable.

Cues are clipped to requested bounds but retain absolute source timestamps; your
manifest/client must map that timeline deliberately. `read` returns bounded text
bytes if needed. Two retained extraction jobs per addon, four per host, five-minute
job deadline, 64 GiB scan budget, 16 MiB result, and 32 KiB read chunks. `cancel`
invalidates serving and stops active work. Poll terminal status and retry `close`
on `subtitles_active` until readers/work release. Context revocation also stops
serving already-extracted protected text.

### Common recoverable errors

| Code/state | What the addon should do |
| --- | --- |
| `permission_denied`, resource not granted | Explain the missing Manager permission/resource; do not retry continuously |
| `feature_unavailable`, `backend_unavailable` | Check capability and supported profile/runtime before showing the action |
| `listener_unavailable` | Close the failed listener; let the user resolve the port/certificate configuration |
| `request_not_found`, `request_claimed` | Drop that completed/disconnected request from pending state |
| `credential_not_found`, destination mismatch | End only the affected logical session; acquire/validate a new correctly scoped context |
| Upstream HTTP 401/403 | Deny client access; do not substitute another client's or owner's token |
| `input_not_seekable`, `position_out_of_range` | Offer a valid position/source instead of decoding an unlimited prefix |
| `stale_track` | Reprobe the selected representation before selecting tracks |
| `stale_generation`, `segment_expired` | Reject stale client work; use current manifests or replace the stream |
| `capacity_exceeded` | Close unused resources or wait for an existing owner to release them |
| `buffer_limit_reached` | Close and explain stalled playback/storage pressure; choose suitable client demand policy |
| `cleanupFailed` | Retry owned close; do not claim capacity or files were released |
| `subtitle_styling_loss` | Deliberately opt into text conversion or choose burn-in |

Use the generic `http-inspector`, `http-bridge`, and `media-stream` examples as
starting points. The media example deliberately accepts local clients only; a
public service addon must add client authentication, authorization, session policy,
protocol-specific manifests and deployment instructions. Those are addon decisions.
Never put tokens, full media paths, signed URLs or incoming headers in diagnostic
messages. The host's default diagnostics avoid those values.


<a id="api"></a>

## 11. Complete API and limits

For JavaScript, use the exact signatures in Appendix A. The table below is the
underlying **broker** contract: its object parameter forms are wire messages,
not positional JavaScript call signatures. For example, broker `host.info`
maps to `ajn.info()`, `log.write` to `ajn.log(message)`, and binary reads are
decoded by the SDK into `Uint8Array`. Normal JavaScript addons do not implement
this transport themselves.

The addon API is versioned separately from AJN, mpv, inference DLLs, and the package's own version. Windows implements this preview. Public messages use no Windows handles or filesystem paths.

### Package and manifest

An `.ajnaddon` is a ZIP containing exactly `manifest.json` and `module.wasm`. No setup script is executed. Only portable core Wasm is accepted, with a maximum module size of 16 MiB and a 16 KiB manifest. Links and extra or nested entries are rejected. The manifest records the module SHA256; registration binds approval to the entire archive SHA256.

See [the example manifest](#tutorial). `build` adds `moduleSha256` to the source manifest. Required fields are `schemaVersion: 1`, lowercase reverse-domain `id`, `name`, `version`, `api: { major: 1, minMinor: 0 }`, `permissions`, and the generated digest. Optional fields:

- `requiredCapabilities`: independently versioned required host features, for example `settings: { major: 1, minMinor: 0 }`. An unavailable requirement prevents startup.
- `activation`: any of `manual`, `on_manager`, `on_player`, `on_login`; defaults to manual. These describe host activation sources, not authority to register startup tasks. The integrated Windows preview supplies the player/Manager events and explicit login opt-in; see [LIFECYCLE.md](#lifecycle). Other embeddings must supply their actual lifecycle sources.
- `settings`: up to 32 definitions of boolean, finite number, bounded string, or choice values. Each has a label and default. Optional descriptions, numeric minimum/maximum, string maximum length, and choice lists are validated by the host.
- `actions`: up to 16 named actions with labels and optional descriptions.
- `sensitiveRequestFields`: API 1.7 `{ headers: string[], query: string[] }` names hidden from incoming listener metadata before event delivery. Declare credential fields here (or in the reviewed listener policy) before using request credential capture.

Unknown top-level metadata is retained for additive evolution. Identity, versions, permissions, typed definitions, duplicate JSON fields, and required capabilities are validated. Unknown or duplicate permission names are rejected. A future required behavior must use capability negotiation or a schema/API version change rather than relying on an unknown optional field.

### Transport profile

The worker uses a bounded newline-delimited **JSON-RPC 2.0** profile over its private stdin/stdout. Messages must be UTF-8 JSON objects, at most 128 KiB before the newline, nesting at most 24. Duplicate object fields are rejected. No batch messages or notifications are accepted in this preview. IDs are nonempty strings up to 80 characters or positive integers no larger than JavaScript's safe integer maximum. All requests receive a matching result or error. This is a deliberately restricted application profile, not a general-purpose JSON-RPC server.

The guest starts by sending:

```json
{"jsonrpc":"2.0","id":"hello","method":"addon.hello","params":{"major":1,"minMinor":0}}
```

The host replies with `result` containing the bound addon ID, API version, approved permissions, features, and a version map named `capabilities`. Optional fields can be added; clients must ignore fields they do not use. A missing optional capability means unavailable. Advertising a capability does not grant its permission.

The host sends an event request:

```json
{"jsonrpc":"2.0","id":"host:1","method":"addon.event","params":{"type":"event","eventId":1,"name":"start","data":{"reason":"manual"}}}
```

While processing it, the guest may issue broker requests and wait for their replies. It then replies to `host:1`. One event runs at a time per worker. Other workers have independent channels. The preview has a two-second event deadline, including broker calls, and at most 128 broker requests per event and 500 per second. A long native operation must return an accepted session/job promptly and run asynchronously behind its handle; it must not block an addon callback for an engine build or video stream's lifetime.

The SDK handles `host.ping` internally. User handlers receive `start`, `stop`, `action` with `{ id }`, `settings.changed`, requested `timer` events, and explicit developer replay events. Additive event-data fields must be ignored unless used. Callback failures stop that worker. A host heartbeat every five seconds detects a guest that stops servicing messages after an event.

API 1.2 adds a binary tail for successful `frames.read` responses; API 1.3 adds one for `network.result`, and API 1.6 uses the frame format for `playerFrames.read`. API 1.7 adds bounded chunks for `httpServer.bodyChunk`, `httpProxy.read`, `mediaProbe.result` and `subtitles.read`. The JSON declares `byteLength`; exactly that many bytes follow the newline. These new chunks are at most 32 KiB and include offset/total/eof metadata. Other messages remain JSON-only. See [FRAMES.md](#frames), [PLAYER-FRAMES.md](#frames), [NETWORK.md](#network) and [STREAMING.md](#streaming).

Errors use standard integer JSON-RPC error codes and an AJN-specific string at `error.data.code`, such as `permission_denied`, `storage_quota`, `capacity_exceeded`, or `feature_unavailable`. Invalid transport/protocol messages stop the worker; valid broker requests that are denied receive a structured error and may be handled by the addon. The JavaScript SDK exposes the string as `error.code`.

### Broker methods

| Method | Parameters | Permission | Result |
| --- | --- | --- | --- |
| `host.info` | `{}` | None | Bound identity, API, permissions, capabilities |
| `settings.get` | `{}` | None | Only this addon's declared effective settings |
| `log.write` | `{ message }` | `log.write` | `null`; control characters stripped |
| `storage.get` | `{ key }` | `storage.read` | JSON value, or `null` for missing key |
| `storage.set` | `{ key, value }` | `storage.write` | `null` after atomic save |
| `sessions.open` | `{ sourceId, profileId? }` | `sessions.manage` | `{ sessionId }`; only if a trusted provider is connected |
| `sessions.status` | `{ sessionId }` | `sessions.manage` | Provider's public JSON status |
| `sessions.close` | `{ sessionId }` | `sessions.manage` | `null` after release |
| `sessions.selections` | `{}` | `sessions.manage` | Approved source/profile IDs and labels; sessions capability 1.1 |
| `sessions.pause` | `{ sessionId, paused }` | `sessions.manage` | `null` after accepting the control; sessions 1.1 |
| `sessions.seek` | `{ sessionId, seconds }` | `sessions.manage` | `null` after accepting an absolute seek; sessions 1.1 |
| `sessions.requestClose` | `{ sessionId }` | `sessions.manage` | `null` after scheduling cleanup; sessions 1.1 |
| `frames.subscribe` | `{ sessionId, stage, format, width, height, maxFps }` | `frames.read` + `sessions.manage` | Subscription ID and accepted sample options; frames 1.0 |
| `frames.read` | `{ subscriptionId }` | `frames.read` + `sessions.manage` | `{ frame, byteLength }` followed by bounded binary bytes; or no new sample |
| `frames.unsubscribe` | `{ subscriptionId }` | `frames.read` + `sessions.manage` | `null`; releases the sample subscription |
| `playerFrames.list` | `{}` | `player.observe` + `frames.read` | Opaque attached player IDs; playerFrames 1.0 |
| `playerFrames.subscribe` | `{ playerId, stage, format, width, height, maxFps }` | `player.observe` + `frames.read` | Owned subscription ID and accepted options |
| `playerFrames.read` | `{ subscriptionId }` | `player.observe` + `frames.read` | Latest unread sample and bounded binary bytes, or no new sample |
| `playerFrames.unsubscribe` | `{ subscriptionId }` | `player.observe` + `frames.read` | `null`; releases only this observer |
| `timers.set` | `{ timerId, intervalMs, repeat }` | None | `null`; creates/replaces a bounded timer; timers 1.0 |
| `timers.clear` | `{ timerId }` | None | `null`; cancels the timer |
| `network.selections` | `{}` | `network.connect` | Approved destination IDs and metadata; network 1.0 |
| `network.request` | `{ destinationId, method, path, headers, bodyBase64, useCredential }` | `network.connect`; `credentials.use` when requested | `{ requestId }`; bounded asynchronous HTTP |
| `network.result` | `{ requestId }` | `network.connect` | Pending/completed/failed status and `byteLength`, then binary body |
| `network.cancel` | `{ requestId }` | `network.connect` | `null`; read terminal result to free the slot |
| `network.sendDatagram` | `{ destinationId, bodyBase64 }` | `network.connect` | `{ bytesSent }`; approved UDP destination only |
| `outputs.formats` | `{}` | `media.output` + `sessions.manage` | Adapter options and resource bounds; outputs 1.0 |
| `outputs.open` | `{ sourceId, profileId, encoding, destination }` | `media.output` + `sessions.manage` + `network.connect`; `credentials.use` when requested | Owned `{ sessionId }`; reviewed source/profile and HTTP upload receiver |
| `remoteSources.formats` | `{}` | `media.input` + `sessions.manage` | Supported sources and limits; remoteSources 1.0 |
| `sessions.openRemote` | `{ source, profileId }` | `media.input` + `sessions.manage` + `network.connect`; `credentials.use` when requested | Owned `{ sessionId }`; approved HTTP source service and profile |
| `outputs.openRemote` | `{ source, profileId, encoding, destination }` | Remote-source permissions plus `media.output` | Owned remote-input/encoded-output session; both services approved independently |

API 1.4 adds typed encoded output controls without changing the binary transport.
Encoded bytes remain in the trusted host; `sessions.status/pause/requestClose`
manage the owned output. See [OUTPUTS.md](#media-io) for exact choices,
destination consent, completion semantics and limits.

API 1.5 adds [remote media sources](#media-io) through the optional
`remoteSources` capability. Local-file methods and older compiled addons keep
their existing behavior. The trusted reader supplies bytes directly to the native
session; a remote media stream does not pass through Wasm messages.

API 1.6 adds [normal-player observations](#frames) through optional
`playerFrames` capability 1.0. These use the existing binary frame transport and
require separate observation consent. Multiple addons share one bounded sample
producer per player while keeping independent subscriptions. Owned-session
methods and previously compiled addons retain their behavior.

#### API 1.7 broker additions

The methods below use the permissions and lifecycle in [STREAMING.md](#streaming).
SDK calls are positional JavaScript methods; these are their underlying wire
parameter objects. All `.formats` methods take `{}`. Long operations return owned
handles promptly; poll them from later callbacks. No encoded media bytes cross
this transport.

| Method | Parameters | Result |
| --- | --- | --- |
| `httpServer.selections` | `{}` | Approved listeners |
| `httpServer.open` | `{ listenerId }` | `{ serverId }` |
| `httpServer.status`, `httpServer.requestClose` | `{ serverId }` | Status / `null` |
| `httpServer.requestStatus`, `httpServer.readBody`, `httpServer.cancelRequest` | `{ requestId }` | Status / `null` |
| `httpServer.extend` | `{ requestId, seconds }` | `null` |
| `httpServer.bodyChunk` | `{ requestId, offset, count }` | Chunk metadata plus bytes |
| `httpServer.respond` | `{ requestId, status, headers, bodyBase64 }` | `null`; claims request |
| `httpServer.beginResponse` | `{ requestId, status, headers }` | `null`; claims buffered response |
| `httpServer.appendResponse` | `{ requestId, bodyBase64 }` | `null`; appends bounded chunk |
| `httpServer.finishResponse` | `{ requestId }` | `null`; completes response |
| `httpProxy.forward` | `{ requestId, destinationId, options }` | `{ operationId }` |
| `httpProxy.buffer` | `{ destinationId, options }` | `{ operationId }`; options use `bodyBase64` on wire |
| `httpProxy.status`, `httpProxy.cancel`, `httpProxy.close` | `{ operationId }` | Status / `null` |
| `httpProxy.read` | `{ operationId, offset, count }` | Chunk metadata plus bytes |
| `requestCredentials.capture` | `{ requestId, destinationId, mappings, seconds? }` | `{ credentialId }` |
| `requestCredentials.status`, `requestCredentials.release` | `{ credentialId }` | Presence/expiry / `null` |
| `mediaProbe.open` | `{ source }` | `{ probeId }` |
| `mediaProbe.status`, `mediaProbe.cancel`, `mediaProbe.close` | `{ probeId }` | Status / `null` |
| `mediaProbe.result` | `{ probeId, offset, count }` | Chunk metadata plus JSON bytes |
| `mediaStreams.open` | `{ source, profileId?, options }` | `{ sessionId, streamId, generationId }` |
| `mediaStreams.status`, `mediaStreams.requestClose` | `{ streamId }` | Status / `null` |
| `mediaStreams.segments` | `{ streamId, cursor?, limit? }` | Segment page |
| `mediaStreams.setDemand` | `{ streamId, seconds }` | `null`; absolute source position |
| `mediaStreams.pause` | `{ streamId, paused }` | `null` |
| `mediaStreams.serve` | `{ requestId, streamId, generationId, resourceId }` | `null`; claims native response |
| `subtitles.open` | `{ source, options }` | `{ subtitleId }` |
| `subtitles.status`, `subtitles.cancel`, `subtitles.close` | `{ subtitleId }` | Status / `null` |
| `subtitles.read` | `{ subtitleId, offset, count }` | Chunk metadata plus WebVTT bytes |
| `subtitles.serve` | `{ requestId, subtitleId }` | `null`; claims native response |

Each capability (`httpServer`, `httpProxy`, `requestCredentials`, `mediaProbe`,
`mediaStreams`, `outputPlayback`, `subtitles`) is independently versioned 1.0.
`outputs.open/openRemote` additionally accept `playback` and `servedStream`
destinations. Local served output requires `media.input` and does not require
`network.connect` unless a remote source/subtitle is selected. Existing upload
signatures/defaults are preserved. Stream session IDs support status, pause and
close through `sessions`; encoded seek returns `operation_unavailable`.

Private storage is separated by addon ID: maximum 256 keys, 32 KiB per value, 1 MiB total. A stored null and a missing key both read as null in this preview. Saving one key is atomic; a read-modify-write sequence is not a transaction across distinct worker instances. The embedding host should create one activation controller per addon ID.

Settings are distinct from private storage. Only trusted UI/CLI code can change them. Unknown saved settings survive removal from a new schema for rollback, but are hidden from the active addon. Invalid/corrupt settings and storage are preserved for recovery. Persistent version-to-version data migrations are intentionally not implicit.

`sourceId` and `profileId` are opaque references supplied by a trusted integration, never an instruction to open an arbitrary path or execute commands. Each worker has an unforgeable host-owned session owner. Session IDs cannot be used by another worker. Closing or failing an addon cancels pending opens and releases its sessions. The library defaults are 16 sessions per registry and four per worker; the native service applies a separate operator-configurable 1–16 session limit (default two). No native capability is advertised unless a trusted AJN runtime is configured. [NATIVE-MEDIA.md](#sessions) specifies approvals, status, and failure semantics. Native consumers should use the new `requestClose` method; legacy `close` retains its synchronous behavior.

### Limits and failure semantics

Workers have no preopened directories, inherited environment, or network grants. They cannot access another addon's files through the broker. Runtime parameters are fixed by the trusted host and verified runtime; addons cannot supply runtime flags or native precompiled code.

Each Windows worker job contains at most two processes (the trusted launcher and Wasmtime), with 512 MiB per process, 768 MiB combined, and a 25% CPU hard cap. Wasm linear memory is additionally capped at 64 MiB. At most eight workers may be active per host process. These are prototype limits to measure and tune, not a promise about future GPU throughput. Stderr is capped at 64 KiB total, retaining up to 8 KiB of diagnostics. Temporary worker files are removed after job termination.

Job objects enforce resource and lifetime limits. Wasmtime enforces the guest filesystem/network capability boundary. AppContainer is additional future defense, not something this implementation claims to use. A trusted native provider must honor cancellation, bound its work, and reliably release its own resources; it cannot be sandboxed by a guest wrapper.

### Compatibility discipline

Before freezing a public major version: ship a versioned SDK/specification, retain old-client contract fixtures, measure real pipeline behavior, and publish a deprecation window. Minor additions must preserve existing field semantics. Changes to timing, color representation, ownership, lifecycle, or permissions need explicit compatibility treatment. Do not expose mpv's raw command channel, private view models, native pointers, or configuration-file layout as the community API.


<a id="cli"></a>

## 12. Developer command reference and replay

These commands use `Invoke-Ajn`, `$runtime` and `$tools` from section 2. Paths
are relative to your extracted bundle. Angle-bracket values in this reference
are placeholders to replace, not literal PowerShell syntax.

```text
new <new-source-directory> <addon-id>
build <source-directory> <javy.exe> <new-package.ajnaddon>
inspect <package.ajnaddon>
install-dev <package.ajnaddon> <test-data-directory> [permission,permission]
run <addon-id> <test-data-directory> <wasmtime.exe> [event-name]
settings <addon-id> <test-data-directory>
configure <addon-id> <test-data-directory> <settings-patch.json>
action <addon-id> <test-data-directory> <wasmtime.exe> <action-id>
replay <addon-id> <test-data-directory> <wasmtime.exe> <events.json>
rollback <addon-id> <test-data-directory>
disable <addon-id> <test-data-directory>
```

`new` and `build` require new destinations. `inspect` validates the package and
prints its manifest/hash; it does not run the addon. `install-dev` grants exactly
the comma-separated permissions supplied, none by default. It cannot approve
native media or arbitrary services. `configure` applies a JSON object of setting
changes. `disable` removes registration while retaining saved data. `run` sends
one event and exits; it is not a background daemon command. `rollback` restores
the previous installed package and its saved grants, retaining current data.

Do not point offline CLI commands at the data directory owned by a running
Manager/host. Use `./addon-work/test-data` for this tutorial. A `host_running`
error protects the active installation. Use Manager to exercise actual media
access, network approvals and long-lived actions. The framework's trusted
service/attachment commands are not needed to create/install an addon.

### Replay a sequence in one worker

Save this as `./addon-work/greeting-events.json`. It works with the tutorial
addon after storage permission has been granted:

```json
{
  "schemaVersion": 1,
  "events": [
    { "name": "start", "data": { "reason": "developer_replay" } },
    { "name": "action", "data": { "id": "greet" } },
    { "name": "action", "data": { "id": "remember" } },
    { "name": "stop", "data": { "reason": "replay_finished" } }
  ]
}
```

```powershell
Invoke-Ajn replay org.example.greeting ./addon-work/test-data $runtime ./addon-work/greeting-events.json
```

Replay is a developer event driver, not a substitute for actual player/network
timing or resource consent. A `settings.changed` replay event alone does not
save configuration; use `configure` to change real settings. Test persistent
state with separate processes too, as the tutorial does.


<a id="testing"></a>

## 13. Testing and troubleshooting

A build passing proves the source can compile, not that it works with a real
player or service. Record expected results before each test, keep the CLI's
data separate from your ordinary installation and test the actual package you
will distribute.

| Check | Expected behavior |
| --- | --- |
| Fresh install, no grants | Plain UI works; protected operations explain missing access |
| Partial grants | Each unavailable feature fails clearly without blocking unrelated actions |
| Empty source/profile/service/player lists | Action explains the required setup; no indexing exception |
| Normal operation | A visible, verifiable result on your target hardware/service |
| Save, exit and restart | Settings/private state survive; active handles are recreated |
| Repeated Start/Stop | No duplicate timers, growing request slots or abandoned sessions |
| User changes settings | New behavior is applied deliberately; any required restart is explained |
| Player closes/service fails | Recoverable message, bounded retry and resource cleanup |
| Cancel and permission/resource removal | Work ends; no continued transmission under removed access |
| Completion/failure | HTTP results consumed; native sessions explicitly closed |
| Update then rollback | Same identity; compatible data retained; renewed resource review handled |
| Two addons/features compete | Capacity errors handled; unrelated work continues |
| Remove then reinstall | Retained settings/data handled; access reviewed again |

For frame addons, use visible moving content (not a black benchmark clip), seek,
open/close a second player, inspect epoch changes and verify BGRA/color/geometry
handling. Test an unsupported format. Measure sample size/rate and resource use.
For output addons, check the received/decoded media and final session state;
an accepted open or `bytesSent` counter does not prove successful delivery.
For service addons, check HTTP status as well as transport completion and test
slow/failed replies. Never use an external mutation as an automatic retry test
unless the service operation is designed to tolerate duplicates.

The first-addon tutorial and embedded examples are taken from the matching
implementation. Release validation extracts code directly from this document,
checks compilation, denied access, process-restart persistence, Unicode settings,
updates and rollback, and builds all nine embedded feature examples.
See the release evidence for the recorded results and native hardware qualification.
That evidence does not validate modifications you make or an outside service's
protocol. API 1.0 compiled-client checks also exist in the host test suite;
compatibility still remains a preview commitment until the public policy is frozen.

Start with the message in Manager's Addons tab. Expand **Technical details** to
copy the error code and message. An error during installation is different from
an error inside a running addon's action. A completed callback also does not mean
an asynchronous media/network operation succeeded; check its terminal status.

### Installation and Manager

| Symptom | What to do |
| --- | --- |
| Addon support files are missing | Extract the complete addon preview into a new writable folder and open its Manager. Keep `addon-host` and its runtime with the player. |
| Addon support becomes unavailable | Choose **Try again**. Your packages/settings are retained. Check state before repeating an install, update or action; the UI does not replay mutations automatically. |
| `invalid_package`, `invalid_manifest`, `invalid_module` | Request a freshly built `.ajnaddon` from the creator. Do not unzip it or substitute a DLL/JavaScript file. Creators: rebuild with the matching tools and inspect the result. |
| `integrity_mismatch` | The file changed after review or its content does not match its digest. Select a fresh, complete package and review it again. |
| `incompatible_api` or an unavailable required capability | Check the creator's supported preview/API/features and use a compatible full AJN build. A newer version number alone does not prove a native adapter is installed. |
| `permission_denied` | Review the same package again through Install addon and choose the intended permissions. Also check its resource access sections. Creators must handle partial grants. |
| Actions are unavailable | Start the addon first. Addons without manual activation need their declared Manager/player/sign-in event. |
| An addon stops after an error | Read its messages, fix the reported setup/input, then choose Start addon to retry. Repeated automatic restarts are deliberately avoided. |
| The previous version cannot start | Review incompatible settings, save valid values, and consult the creator about persistent-data compatibility. Rollback retains data. |
| Changing addons is blocked by unsaved settings | Save your edits, or choose Reload and confirm discarding them. Cancelling the prompt keeps the draft. |
| A stopped addon starts when another player opens | Stop ends current work; the addon still has its declared startup events. Remove it to prevent future activation. |

### Sources, networking and playback

| Symptom or code | Meaning and recovery |
| --- | --- |
| `source_not_granted`, `profile_not_granted`, `destination_not_granted` | Approve the specific resource for this package in its access section. Updates can require renewed resource consent. Creators should re-enumerate selections. |
| `capacity_exceeded` | A worker/session/request/subscription quota is full. Close unused work, including terminal request/session handles. Performance limits changes native session admission only; it does not raise every quota. |
| `player_not_found`, `player_closed` | The original player ended or its attachment disappeared. Open a player, list again and subscribe using its current opaque ID. |
| Player list is empty | Open a video after installing the first addon. Check the matching full preview and observation permissions. Ordinary playback can continue while no addon bridge exists. |
| A frame read returns `null` | No new sample is available. Return from the callback and try on a future timer. Do not block or treat it as black pixels. |
| `frame_format_unavailable` | The sample producer cannot handle that source/backend. The preview supports the documented progressive mono SDR D3D11 path, not every HDR/TensorRT/final-display case. |
| `frame_unavailable` | Native sample production failed. Playback may continue. Stop observing, review logs/hardware support and retry only after addressing the problem. |
| `session_not_found`, `subscription_not_found` | The handle was closed, belongs to another worker or is stale. Do not persist/reuse live handles across restarts. |
| HTTP request remains pending | Poll it from later timer events within the documented deadlines. Do not loop inside one callback. Consume the terminal result even after cancellation. |
| HTTP returns a non-success status | The service responded but rejected/failed the operation. Inspect status and a bounded response body; avoid logging private response contents. |
| Remote media cannot seek | The source may lack stable validators/range support. Check the input status; forward-only input is valid. |
| Output open is accepted but produces no stream | Check session status, approved receiver, profile and the supported NVENC codec/container/audio combination. Acceptance is not delivery completion. |
| Model initialization takes time | Let the native session progress outside the callback. Engine preparation is not a reason to extend a guest callback beyond its time budget. |

### Creator build/runtime mistakes

Use PowerShell 7 and the tools from the same preview. `compiler_mismatch` or
`runtime_mismatch` means the executable does not match the pinned tool expected
by the host. Run the provided bootstrap and use its paths; do not disable hash
checks to make a different binary run.

`already_exists` during `new` or `build` means the command is protecting an
existing source folder/package. Choose a new folder or output filename.
Keep CLI test data separate from a running Manager's data directory.

If JavaScript mentions missing `fetch`, `require`, `setInterval` or DOM objects,
it was written for another runtime. Use the SDK calls described in the
[creator guide](#tutorial). A promise returned from `onEvent` is rejected;
use synchronous callbacks and host timers. For unexpected process termination,
check the callback deadline, message/binary sizes, CPU/memory quotas and logs in
[API limits](#api).

When reporting a creator issue, include a minimal source example, manifest,
build command, AJN/API/runtime versions, reproducible steps and the exact error.
For native media include the backend, GPU/driver and non-private source format.
Avoid service secrets, full authenticated URLs and personal media paths in public
reports. [Recipes](#recipes) links the complete maintained examples.


<a id="publishing"></a>

## 14. Versioning and publishing your addon

Keep source under version control and use your own permanent addon ID. Set the
minimum API minor/capabilities for the features you actually call; the host
application's version is not the API requirement. Optional functionality should
check capability versions and actual permission grants, then provide a fallback.
Do not guess support from a DLL filename or private configuration layout.

Before distributing an update, increase the package's version, build to a new
filename and test both upgrade and rollback against existing saved data. Never
change the bytes of a file while a user is reviewing it. Resource approvals bind
to that exact package; tell users which selections need renewed review.

Distribute the `.ajnaddon` plus a short README describing:

- What the addon does, its version and license, its author/source location and
  where users can report problems.
- Compatible AJN release/API/capabilities and the operating systems, GPU/backend,
  media formats, device models or service versions actually tested.
- Every permission, why it is needed and which features work if it is denied.
- Exact Manager setup, file/profile/service/credential approvals, settings,
  actions, expected results and how to stop the work.
- Persistent-data compatibility, update/rollback steps and known limits.

Never include DLLs, native executables or setup scripts in the package. It must
contain only the generated manifest and portable module. Javy/Wasmtime/.NET
installation is not an end-user addon step: the matching AJN release provides
the runtime. Avoid credentials in source, settings, sample files or screenshots.
Website catalog installation, publisher signing and automatic addon updates are
future work; currently users install local files through Manager.

Public extension points are the documented guest API and manifest. Private
native ABI markers, management IPC, internal view models and mpv configuration
paths are not an addon contract. If a feature needs a missing API, describe the
capability needed to the maintainers instead of making an addon depend on an
internal component. Linux is a future implementation target, not a supported
runtime of this Windows release.


<a id="llm"></a>

## 15. Instructions for an LLM-assisted project

Attach this whole Markdown file to a new chat and supply your desired behavior.
The LLM does not need the original AJN conversations. Give it the external
service/device documentation if your idea involves one. Use this prompt:

> Create an AnimeJaNai addon using the attached developer guide as the AJN
> specification. My addon should: [describe the behavior, controls and result].
> First map each requirement to a documented capability and identify any
> unsupported requirement. Ask only for product choices or outside-service
> facts that cannot be inferred. Do not invent AJN APIs, Node/browser functions,
> native helpers or access to internal files. Use synchronous `onEvent` callbacks,
> host timers and bounded work. Handle missing capabilities, denied permissions,
> empty selections, cancellation, terminal results and resource cleanup. Keep
> credentials in Manager's scoped credential flow. Deliver complete `addon.js`
> and `manifest.json`, the unchanged Appendix A `ajn.d.ts` if editor types are
> needed, and a README with exact build/install/test instructions. Use an original
> addon ID and explain every permission. Preserve state through update/rollback.
> State which checks you actually ran and which need my machine or service.

A useful delivery is source files and reproducible commands, not a claimed
compiled package that the chat did not build. If it cannot execute tools, save
its files locally and follow section 2 yourself. Begin with the smallest working
feature, verify it, then add capabilities. Stop to resolve an unsupported
requirement rather than accepting an invented SDK call.

Check that generated code uses the positional JavaScript signatures in Appendix
A, not the underlying broker parameter objects in section 11. `ajn` is supplied
only inside the callback; do not call it at global initialization. Global state
may retain live handles between events, but persisted storage must not be used
to resurrect them after a restart.


<a id="sdk"></a>

## Appendix A. Complete JavaScript editor declarations

Save the following block verbatim as `ajn.d.ts` beside `addon.js` if the scaffold
has not already created it. These are the exact API 1.7 declarations from this
edition. They describe signatures, not grants: every permission, ownership,
capability and limit described above still applies. `sessions.status` deliberately
uses an open record; inspect its documented state and optional fields defensively.

```typescript
/** AJN addon API 1.8 development. Plain JavaScript, with optional editor type checking. */
interface AjnScenePair {
    requestId: string; epoch: string;
    previousPtsSeconds: number; currentPtsSeconds: number;
    width: number; height: number; sourceWidth: number; sourceHeight: number;
    format: "gray8"; stage: "beforeInterpolation"; range: "full";
    /** Remaining native decision budget at the time the host copied the pair. */
    remainingMs: number;
    /** Two row-major width*height grayscale arrays. Encoded SDR, no OSD/subtitles. */
    previous: Uint8Array; current: Uint8Array;
}
interface AjnRemoteSource {
    type?: "http"; destinationId: string; path?: string; useCredential?: boolean;
    /** API 1.7: temporary request-derived context. Mutually exclusive with useCredential. */
    credentialId?: string;
}
type AjnProbeSource = { type: "local"; sourceId: string } | AjnRemoteSource;
interface AjnProbeResult {
    representationId: string; container: string | null; durationSeconds: number | null;
    startSeconds: number | null; seekable: boolean;
    tracks: AjnProbeTrack[];
    chapters: { startSeconds: number | null; endSeconds: number | null; title: string | null }[];
    attachments: { streamIndex: number; name: string | null; mimeType: string | null; byteLength: number; font: boolean }[];
}
interface AjnProbeTrack {
    trackId: string; streamIndex: number; typeOrdinal: number;
    type: "video" | "audio" | "subtitle"; codec: string | null;
    language: string | null; title: string | null; default: boolean; forced: boolean; startSeconds: number | null;
    width?: number | null; height?: number | null;
    pixelFormat?: string | null;
    pixelAspectRatio?: AjnRatio | null; displayAspectRatio?: AjnRatio | null;
    averageFrameRate?: AjnRatio | null; nominalFrameRate?: AjnRatio | null;
    /** null means the bounded probe cannot establish CFR or VFR. */
    variableFrameRate?: boolean | null; fieldOrder?: "progressive" | "interlaced" | null;
    colorPrimaries?: string | null; colorTransfer?: string | null; colorMatrix?: string | null; colorRange?: string | null;
    rotationDegrees?: number | null; hasMasteringDisplayMetadata?: boolean; hasContentLightMetadata?: boolean;
    channels?: number | null; sampleRate?: number | null; channelLayout?: string | null;
    subtitleKind?: "text" | "bitmap" | null;
}
interface AjnRatio { numerator: number; denominator: number; }
interface AjnRemoteFormats {
    types: string[]; protocols: string[]; containers: string[]; playlists: boolean; redirects: boolean; seek: string;
    maximumBytes: number; maximumBytesPerSecond: number; maximumReadBytes: number; maximumRequests: number;
    ioDeadlineSeconds: number; maximumWallSeconds: number; maximumConcurrentSessions: number;
}
interface AjnOutputOptions {
    encoding: {
        videoCodec: "h264" | "hevc" | "av1"; container: "matroska" | "mpegts" | "fragmentedMp4";
        videoKbps: number; audioCodec?: "none" | "aac" | "opus"; audioKbps?: number;
        keyframeFrames?: number; lengthSeconds?: number; audioChannels?: 2;
    };
    destination: {
        type?: "httpUpload"; destinationId: string; path?: string; method?: "POST" | "PUT";
        useCredential?: boolean;
    } | { type: "servedStream"; mode?: "segments" | "continuous"; segmentSeconds?: number };
    playback?: AjnOutputPlayback;
}
interface AjnOutputPlayback {
    startSeconds?: number;
    audioTrack?: "default" | "none" | { trackId: string };
    subtitles?: { mode: "none" } | { mode: "burn"; trackId: string } | { mode: "burn"; externalSourceId: string }
        | { mode: "burn"; externalRemoteSource: AjnRemoteSource };
}
interface AjnStreamOptions {
    encoding: AjnOutputOptions["encoding"]; playback?: AjnOutputPlayback;
    mode?: "segments" | "continuous"; segmentSeconds?: number;
}
interface AjnStreamHandle { sessionId: string; streamId: string; generationId: string; }
interface AjnStreamSegment {
    resourceId: string; generationId: string; sequence: number;
    sourceStartSeconds: number; sourceEndSeconds: number; durationSeconds: number;
    encodedTimestampOriginSeconds: number | null; byteLength: number;
    independent: boolean; initializationId: string | null; etag: string;
}
interface AjnStreamPage {
    generationId: string; segments: AjnStreamSegment[]; nextCursor: string;
    initializationId: string | null; continuousResourceId: string | null;
}
interface AjnStreamStatus extends AjnStreamHandle {
    state: "opening" | "starting" | "probing" | "loadingSubtitles" | "loading" | "running" | "finishing" | "paused" | "bufferPaused" | "producerCompleted" | "closing" | "closed" | "failed" | "cleanupFailed";
    nativeCapacityReleased: boolean; requestedStartSeconds: number; demandPositionSeconds: number; userPaused: boolean;
    retainedStartSeconds: number | null; retainedEndSeconds: number | null;
    native: Record<string, unknown>; transfer: Record<string, number>;
    /** Measured from a completed encoded object; null until one is inspected (continuous: at EOF). */
    encodedMedia: { container: string | null; durationSeconds: number | null; startSeconds: number | null;
        tracks: Omit<AjnProbeTrack, "trackId" | "language" | "title">[] } | null;
    measurements: { processingMediaSecondsPerWallSecond: number | null; speedMeasurementWallSeconds: number | null;
        effectiveBitrateKbps: number | null; bitrateMeasurementMediaSeconds: number | null; bitrateBasis: string };
    error: { code: string; message: string } | null;
}
interface AjnOutputFormats {
    encoders: string[]; requires: string; videoCodecs: string[]; containers: string[]; audioCodecs: string[]; destinations: string[];
    mpegtsVideoCodecs: string[]; mpegtsAudioCodecs: string[];
    minimumVideoKbps: number; maximumVideoKbps: number; minimumAudioKbps: number; maximumAudioKbps: number;
    minimumKeyframeFrames: number; maximumKeyframeFrames: number;
    maximumWallSeconds: number; maximumBytes: number; maximumBytesPerSecond: number; maximumHostBytesPerSecond: number;
    maximumConcurrentSessions: number; softwareSubtitles: boolean;
}
interface AjnNetworkDestination {
    id: string; name: string; origin: string; protocol: "http" | "https" | "udp";
    addresses: string[]; hasCredential: boolean; credentialHeader: string | null;
}
interface AjnHttpOptions {
    method?: "GET" | "HEAD" | "POST" | "PUT" | "PATCH" | "DELETE" | "OPTIONS";
    path?: string; headers?: Record<string, string>;
    /** At most 32 KiB. Strings are encoded as UTF-8. */
    body?: Uint8Array | string;
    useCredential?: boolean;
}
interface AjnNetworkResult {
    state: "pending" | "completed" | "failed";
    status?: number; headers?: Record<string, string>;
    error?: { code: string; message: string };
    /** At most 64 KiB. Empty while pending or failed. */
    body: Uint8Array;
}
interface AjnFrame {
    frameId: string;
    epoch: string;
    width: number;
    height: number;
    stride: number;
    format: "bgra8";
    stage: "processed";
    /** Processing PTS, not measured display time. */
    ptsSeconds: number | null;
    producerTimeMs: number;
    sourceWidth: number;
    sourceHeight: number;
    rotation: number;
    verticalFlip: boolean;
    pixelAspectRatio: number;
    crop: { x: number; y: number; width: number; height: number };
    color: { primaries: string; transfer: string; matrix: "rgb"; range: "full"; alpha: "opaque" };
    skippedSamples: number;
    producerDrops: number;
    /** Tightly packed bytes, top row first. Do not return pixels as event JSON. */
    pixels: Uint8Array;
}
interface AjnSampleOptions {
    stage?: "processed";
    format?: "bgra8";
    width?: number;
    height?: number;
    maxFps?: number;
}
interface AjnHostInfo {
    id: string;
    api: { major: number; minor: number };
    permissions: string[];
    features: string[];
    capabilities: Record<string, { major: number; minor: number }>;
}
interface AjnProxyOptions {
    path?: string;
    requestHeaders?: { name: string; values: string[] | null }[];
    responseHeaders?: { name: string; values: string[] | null }[];
    /** Explicitly opt in to incoming sensitive headers and outgoing Set-Cookie. */
    passRequestHeaders?: string[];
    passResponseHeaders?: string[];
    useCredential?: boolean;
    credentialId?: string;
}
interface AjnListenerBinding {
    address: string;
    port: number;
    sensitiveHeaders: string[];
    sensitiveQuery: string[];
    scheme: "http" | "https";
    scope: "loopback" | "lan" | "public";
    certificateId: string | null;
    certificateHost: string | null;
    publicBaseUrl: string | null;
    allowedHosts: string[] | null;
    cors: { origins: string[]; methods: string[]; headers: string[]; exposeHeaders: string[]; allowCredentials: boolean } | null;
}
interface AjnApi {
    /** API 1.8, sceneDetection 1.0. Requires player.sceneDetection and frames.read.
     * One exclusive detector per player; callbacks remain inside the addon sandbox.
     * The host delivers scene.request events containing {detectorId}. */
    sceneDetection: {
        list(): { playerId: string; inUse: boolean; format: "gray8"; stage: "beforeInterpolation" }[];
        attach(playerId: string, options?: { width?: number; height?: number; deadlineMs?: number }): {
            detectorId: string; width: number; height: number; deadlineMs: number; format: "gray8"; stage: "beforeInterpolation";
        };
        /** Latest pending pair once, or null if consumed, expired, reset or unavailable. */
        read(detectorId: string): AjnScenePair | null;
        /** false means late, duplicate or stale. It never applies to a later pair. */
        submit(detectorId: string, requestId: string, decision: "cut" | "continuous" | "default"): { accepted: boolean };
        status(detectorId: string): {
            state: "waitingForPlayer" | "pending" | "active" | "fallback" | "sampleUnavailable" | "backendUnavailable" | "suspended" | "unavailable";
            acceptedPairs: number; timedOutPairs: number;
        };
        detach(detectorId: string): void;
    };
    info(): AjnHostInfo;
    /** API 1.7 development: explicitly approved HTTP(S) listeners. */
    httpServer: {
        selections(): { listeners: { id: string; name: string; binding: AjnListenerBinding }[] };
        formats(): { protocols: string[]; scope: string; maximumInlineBytes: number; maximumBufferedBytes: number; maximumRequests: number; maximumActiveRequests: number; maximumListeners: number; maximumConnectionsPerListener: number; decisionSeconds: number; maximumDecisionSeconds: number; requestBodyReading: boolean; webSockets: boolean; cors: string };
        open(listenerId: string): { serverId: string };
        status(serverId: string): { serverId: string; listenerId: string; state: string; publicBaseUrl: string | null; reachability: "unverified"; certificateExpires: string | null; error: { code: string; message: string } | null };
        requestClose(serverId: string): void;
        requestStatus(requestId: string): { requestId: string; state: "pending" | "claimed"; bodyState: "unread" | "reading" | "ready" | "failed"; bodyLength: number | null; bodyError: string | null; decisionRemainingSeconds: number | null };
        cancelRequest(requestId: string): void;
        /** Extend from now, capped at 120 seconds after arrival. Does not renew an expired request. */
        extend(requestId: string, seconds: number): void;
        readBody(requestId: string): void;
        bodyChunk(requestId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        /** Begin claims the request. Append chunks up to 32 KiB, total up to 256 KiB, then finish within the decision deadline. */
        beginResponse(requestId: string, response: { status: number; headers?: { name: string; values: string[] }[] }): void;
        appendResponse(requestId: string, body: Uint8Array | string): void;
        finishResponse(requestId: string): void;
        /** Claims the request once; up to 32 KiB body. Framing and CORS headers are host-owned. */
        respond(requestId: string, response: { status: number; headers?: { name: string; values: string[] }[]; body?: Uint8Array | string }): void;
    };
    log(message: string): void;
    /** API 1.7 development. Requires network.proxy and network.connect; forward also requires network.listen. */
    httpProxy: {
        formats(): { nativeForwarding: boolean; webSockets: boolean; maximumBufferedBytes: number; maximumChunkBytes: number; maximumOperations: number; maximumBufferedOperations: number; headerTimeoutSeconds: number; ioTimeoutSeconds: number; maximumLifetimeSeconds: number; maximumTransferBytes: number; maximumWebSocketMessageBytes: number };
        forward(requestId: string, destinationId: string, options?: AjnProxyOptions): { operationId: string };
        buffer(destinationId: string, options?: AjnProxyOptions & { method?: string; body?: Uint8Array | string; maximumBytes?: number }): { operationId: string };
        status(operationId: string): { operationId: string; state: "pending" | "completed" | "failed"; status: number | null; headers: { name: string; values: string[] }[]; bodyLength: number | null; representationLength: number | null; cleanupReady: boolean; error: { code: string; message: string } | null };
        read(operationId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        cancel(operationId: string): void;
        /** Wait for cleanupReady before releasing a completed/cancelled operation. */
        close(operationId: string): void;
    };
    /** API 1.7 development. Capture is not authentication. Validate the client with the upstream before serving protected content. */
    requestCredentials: {
        formats(): { maximumContexts: number; maximumLifetimeSeconds: number; maximumFields: number; maximumBytes: number; captureAuthenticatesClient: false };
        capture(requestId: string, destinationId: string, mappings: { from: "header" | "query"; name: string; to: "header" | "query"; target: string }[], seconds?: number): { credentialId: string };
        status(credentialId: string): { credentialId: string; destinationId: string; expires: string; present: true; authenticated: false; fields: { kind: string; name: string; present: true }[] };
        release(credentialId: string): void;
    };
    /** Host-owned declarative settings. Only the user/host can change them. */
    settings: { get(): Record<string, boolean | number | string> };
    /** API 1.5, remoteSources capability 1.0 and media.input permission.
     * The trusted reader streams media; bytes never enter the Wasm runtime. */
    remoteSources: { formats(): AjnRemoteFormats };
    /** API 1.7. Probes have their own capacity and do not start an encoder. */
    mediaProbe: {
        formats(): { maximumJobs: number; maximumSeconds: number; maximumResultBytes: number; maximumChunkBytes: number; startsEncoder: false };
        open(source: AjnProbeSource): { probeId: string };
        status(probeId: string): { probeId: string; state: "pending" | "completed" | "failed"; byteLength: number | null; error: { code: string; message: string } | null };
        /** Complete result, assembled through at most eight bounded native reads. Wait for completed status first. */
        result(probeId: string): AjnProbeResult;
        read(probeId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; totalBytes: number; offset: number; eof: boolean };
        cancel(probeId: string): void;
        /** Cancel if active, then wait for terminal status before closing. */
        close(probeId: string): void;
    };
    /** Requires media.input and sessions.manage; remote sources also require network.connect.
     * Source consent and request credential scope are checked independently. */
    subtitles: {
        formats(): { burn: boolean; extract: string[]; textCodecs: string[]; bitmapExtraction: boolean; preservesAssLayout: boolean;
            timestampTimeline: "source"; maximumJobs: number; maximumHostJobs: number; maximumResultBytes: number; maximumChunkBytes: number; maximumSeconds: number };
        /** Omit trackId only for a standalone approved subtitle file. Text conversion loses ASS positioning/fonts/drawing. */
        open(source: AjnProbeSource, options?: { trackId?: string; startSeconds?: number; endSeconds?: number; allowStylingLoss?: boolean }): { subtitleId: string };
        status(subtitleId: string): { subtitleId: string; state: "pending" | "completed" | "failed"; byteLength: number | null;
            contentType: string; timestampTimeline: "source"; error: { code: string; message: string } | null };
        read(subtitleId: string, offset: number, count?: number): { body: Uint8Array; byteLength: number; offset: number; totalBytes: number; eof: boolean };
        /** Authorize each client request first; the host serves WebVTT bytes directly. */
        serve(requestId: string, subtitleId: string): void;
        cancel(subtitleId: string): void;
        /** Cancel and wait for pending work/responses first. Retry subtitles_active until cleanup completes. */
        close(subtitleId: string): void;
    };
    mediaStreams: {
        formats(): Record<string, unknown>;
        open(source: AjnProbeSource, profileId: string | null, options: AjnStreamOptions): AjnStreamHandle;
        status(streamId: string): AjnStreamStatus;
        segments(streamId: string, cursor?: string | null, limit?: number): AjnStreamPage;
        /** Absolute source playback position, within the produced timeline. Refresh while paused to retain ownership. */
        setDemand(streamId: string, seconds: number): void;
        pause(streamId: string, paused: boolean): void;
        /** Authorize the client before each call. Handles confer no client authentication. */
        serve(requestId: string, streamId: string, generationId: string, resourceId: string): void;
        requestClose(streamId: string): void;
    };
    outputs: {
        open(sourceId: string, profileId: string | null, options: AjnOutputOptions & { destination: { type: "servedStream" } }): AjnStreamHandle;
        openRemote(source: AjnRemoteSource, profileId: string | null, options: AjnOutputOptions & { destination: { type: "servedStream" } }): AjnStreamHandle;
        formats(): AjnOutputFormats;
        open(sourceId: string, profileId: string | null, options: AjnOutputOptions): { sessionId: string };
        /** Also requires media.input and remoteSources capability 1.0.
         * Input and receiver must each be independently approved. */
        openRemote(source: AjnRemoteSource, profileId: string | null, options: AjnOutputOptions): { sessionId: string };
    };
    outputPlayback: { formats(): { startOffsets: boolean; audioTrackSelection: boolean; audioChannels: number[]; subtitleModes: string[];
        externalSubtitleSources: string[]; maximumExternalSubtitleBytes: number; seekStrategy: "closeAndReopen"; maximumStartSeconds: number } };
    /** Requires network.connect plus destination consent. Saved credentials
     * also require credentials.use. No redirects, cookies or OS credentials. */
    network: {
        selections(): { destinations: AjnNetworkDestination[] };
        request(destinationId: string, options?: AjnHttpOptions): { requestId: string };
        /** A completed/failed result is consumed exactly once and frees its slot. */
        result(requestId: string): AjnNetworkResult;
        /** Poll the terminal result to release the cancelled request's slot. */
        cancel(requestId: string): void;
        /** At most 16 KiB; success means sent, not acknowledged by the device. */
        sendDatagram(destinationId: string, bytes: Uint8Array | string): { bytesSent: number };
    };
    /** Timer events have data {timerId, elapsedMs, missedTicks}. All callbacks are
     * serialized. Missed ticks coalesce; eight timers and 60 background events/s. */
    timers: {
        set(timerId: string, intervalMs: number, repeat?: boolean): void;
        clear(timerId: string): void;
    };
    /** Requires frames.read, sessions.manage and frames capability 1.0. */
    frames: {
        subscribe(sessionId: string, options?: AjnSampleOptions): Required<AjnSampleOptions> & { subscriptionId: string };
        /** Latest unread sample, or null. Never waits for the GPU. */
        read(subscriptionId: string): AjnFrame | null;
        unsubscribe(subscriptionId: string): void;
    };
    /** API 1.6, playerFrames capability 1.0. Requires player.observe and
     * frames.read separately from owned-session permissions. No player controls
     * or filenames are exposed. Samples precede final display composition. */
    playerFrames: {
        list(): { playerId: string; stage: "processed"; format: "bgra8" }[];
        subscribe(playerId: string, options?: AjnSampleOptions): Required<AjnSampleOptions> & { subscriptionId: string };
        /** Independent latest unread sample, or null. Slow readers skip frames. */
        read(subscriptionId: string): AjnFrame | null;
        unsubscribe(subscriptionId: string): void;
    };
    storage: {
        /** Missing keys return null. Values must be JSON-serializable. */
        get(key: string): unknown;
        set(key: string, value: unknown): void;
    };
    sessions: {
        /** Sessions capability 1.1. Only user-approved resources, never paths. */
        selections(): {
            sources: { id: string; name: string }[];
            profiles: { id: string; name: string; slot: number; backend: string }[];
            maximumConcurrentSessions: number;
        };
        /** Requires sessions.manage and a trusted native provider in the host. */
        open(sourceId: string, profileId?: string | null): { sessionId: string };
        /** API 1.5: media.input, sessions.manage, network.connect and an approved
         * HTTP service/profile. credentials.use is required when selected.
         * Check status.input.seekable before seeking; some streams are forward-only. */
        openRemote(source: AjnRemoteSource, profileId?: string | null): { sessionId: string };
        status(sessionId: string): Record<string, unknown>;
        /** Accepted asynchronously; observe status for the resulting state. */
        pause(sessionId: string, paused: boolean): void;
        /** Absolute media seconds. Sessions capability 1.1. */
        seek(sessionId: string, seconds: number): void;
        /** Legacy synchronous release. Use requestClose for native sessions. */
        close(sessionId: string): void;
        /** Sessions 1.1: starts cleanup immediately. Capacity remains reserved
         * until cleanup finishes. Status then returns session_not_found. */
        requestClose(sessionId: string): void;
    };
}
interface AjnEvent { type: "event"; eventId: number; name: string; data: unknown; }
/** Implement this callback in addon.js. Return a JSON value or undefined. */
declare function onEvent(event: AjnEvent, ajn: AjnApi): unknown;
```


<a id="examples"></a>

## Appendix B. Complete feature examples

Each subsection below contains the **complete** manifest and JavaScript for one
shipped diagnostic example. These demonstrate AJN plumbing; they are not finished
integrations for a particular media service or lighting device. Copy both files
into one source folder and use Appendix A for editor types. Choose only one
example per folder and change its ID/name before publishing your own version.

After completing section 2, you can scaffold and build a copied example:

```powershell
Invoke-Ajn new ./addon-work/my-feature org.example.myfeature
# Replace my-feature/manifest.json and addon.js with a complete pair below.
# Set the manifest id to org.example.myfeature and use your own display name.
Invoke-Ajn build ./addon-work/my-feature $tools.javy.path ./addon-work/my-feature-0.1.0.ajnaddon
Invoke-Ajn inspect ./addon-work/my-feature-0.1.0.ajnaddon
```

Install in the full preview's Manager, grant only the permissions you want to
test, approve the relevant resources, start the addon and use its actions.
Ordinary standalone CLI invocation does not provide native media or service
approvals. Stop the addon between tests to release its resources. Examples may
return structured technical information; give your finished addon readable
messages. Review error recovery for your application rather than assuming a
diagnostic example covers every service, format and production workload.

<a id="example-session-controller"></a>

### session-controller

**manifest.json**

<!-- release-example:session-controller/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.session-controller",
  "name": "Session controller example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 1 },
  "permissions": ["sessions.manage"],
  "requiredCapabilities": { "sessions": { "major": 1, "minMinor": 1 } },
  "activation": ["manual"],
  "settings": {
    "sessionCount": { "type": "number", "label": "Sessions to open", "description": "Uses the first approved file and saved profile. The host may have less free capacity.", "default": 1, "minimum": 1, "maximum": 16 },
    "controlledSession": { "type": "number", "label": "Session to control", "description": "1 selects the first session, 2 the second, and so on.", "default": 1, "minimum": 1, "maximum": 16 }
  },
  "actions": {
    "resources": { "label": "Show approved media" },
    "open": { "label": "Open sessions", "description": "Start this addon first so its sessions remain active between actions." },
    "status": { "label": "Show session status" },
    "pause": { "label": "Pause selected session" },
    "resume": { "label": "Resume selected session" },
    "seek": { "label": "Seek selected session to start" },
    "close": { "label": "Close sessions" }
  }
}
```

**addon.js**

<!-- release-example:session-controller/addon.js -->
```javascript
// Generic API example, not a Plex or lighting implementation.
let sessions = [];
function onEvent(event, ajn) {
    function closeAll() {
        for (const id of sessions) {
            try { ajn.sessions.requestClose(id); }
            catch (error) { if (error.code !== "session_not_found") throw error; }
        }
        sessions = [];
    }
    if (event.name === "stop") { closeAll(); return; }
    if (event.name !== "action") return;
    const settings = ajn.settings.get();
    try {
        switch (event.data.id) {
            case "resources": return ajn.sessions.selections();
            case "open": {
                if (sessions.length) return { message: "Close existing sessions first.", sessions };
                const selected = ajn.sessions.selections();
                if (!selected.sources.length || !selected.profiles.length)
                    return { message: "Approve a media file and a saved profile in Manager first." };
                if (!Number.isInteger(settings.sessionCount)) return { message: "Choose a whole number of sessions." };
                for (let i = 0; i < settings.sessionCount; i++)
                    sessions.push(ajn.sessions.open(selected.sources[0].id, selected.profiles[0].id).sessionId);
                return { message: "Sessions are initializing. Use Show session status.", sessions };
            }
            case "status": return sessions.map(id => ({ id, ...ajn.sessions.status(id) }));
            case "close": closeAll(); return { message: "Session cleanup requested." };
        }
        const index = settings.controlledSession - 1;
        if (!Number.isInteger(index) || !sessions[index]) return { message: "Choose an existing session number." };
        if (event.data.id === "pause") ajn.sessions.pause(sessions[index], true);
        if (event.data.id === "resume") ajn.sessions.pause(sessions[index], false);
        if (event.data.id === "seek") ajn.sessions.seek(sessions[index], 0);
        return { message: "Control accepted. Use Show session status to see the result." };
    } catch (error) {
        return { error: error.code || "operation_failed", message: error.message, sessions };
    }
}
```

<a id="example-sample-inspector"></a>

### sample-inspector

**manifest.json**

<!-- release-example:sample-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.sample-inspector",
  "name": "Frame sample inspector",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 2 },
  "permissions": ["sessions.manage", "frames.read"],
  "requiredCapabilities": { "frames": { "major": 1, "minMinor": 0 }, "timers": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "sessionCount": { "type": "number", "label": "Sessions to sample", "default": 1, "minimum": 1, "maximum": 16 },
    "width": { "type": "number", "label": "Sample width", "default": 64, "minimum": 1, "maximum": 320 },
    "height": { "type": "number", "label": "Sample height", "default": 36, "minimum": 1, "maximum": 180 },
    "maxFps": { "type": "number", "label": "Maximum samples per second", "description": "Reopen sessions to apply changed sample settings.", "default": 30, "minimum": 1, "maximum": 60 }
  },
  "actions": {
    "open": { "label": "Start samples", "description": "Start the addon first, then approve a local file and a DirectML profile." },
    "status": { "label": "Show sample information" },
    "close": { "label": "Close sample sessions" }
  }
}
```

**addon.js**

<!-- release-example:sample-inspector/addon.js -->
```javascript
// Generic API example. No network destinations or device-specific behavior.
let sessions = [];

function closeSamples(ajn) {
    ajn.timers.clear("samples");
    for (const item of sessions) {
        if (item.subscription) {
            try { ajn.frames.unsubscribe(item.subscription); } catch (_) {}
        }
        try { ajn.sessions.requestClose(item.id); } catch (_) {}
    }
    sessions = [];
}

function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Ready. Approve a file and DirectML profile, then choose Start samples." };
    if (event.name === "stop") { closeSamples(ajn); return; }
    if (event.name === "timer" && event.data.timerId === "samples") {
        for (const item of sessions) {
            if (!item.subscription) continue;
            try {
                const frame = ajn.frames.read(item.subscription);
                if (!frame) continue;
                item.received++;
                const center = (Math.floor(frame.height / 2) * frame.width + Math.floor(frame.width / 2)) * 4;
                item.last = {
                    frameId: frame.frameId, epoch: frame.epoch, ptsSeconds: frame.ptsSeconds,
                    sample: [frame.width, frame.height], source: [frame.sourceWidth, frame.sourceHeight],
                    color: frame.color, rotation: frame.rotation, verticalFlip: frame.verticalFlip,
                    skippedSamples: frame.skippedSamples, producerDrops: frame.producerDrops,
                    binaryBytes: frame.pixels.length, centerBGRA: Array.from(frame.pixels.subarray(center, center + 4)),
                };
            } catch (error) {
                item.error = error.code || String(error.message);
                try { ajn.frames.unsubscribe(item.subscription); } catch (_) {}
                item.subscription = null;
                try { ajn.sessions.requestClose(item.id); } catch (_) {}
            }
        }
        if (!sessions.some(item => item.subscription)) ajn.timers.clear("samples");
    }
    if (event.name !== "action") return;
    if (event.data.id === "close") { closeSamples(ajn); return { message: "Session cleanup requested." }; }
    if (event.data.id === "open") {
        if (sessions.length) return { message: "Close existing sample sessions before reopening them." };
        const selected = ajn.sessions.selections(), settings = ajn.settings.get();
        if (!selected.sources.length || !selected.profiles.length) return { message: "Approve a local file and a DirectML profile first." };
        let error = null;
        for (let i = 0; i < Math.floor(settings.sessionCount); i++) {
            let id = null;
            try {
                id = ajn.sessions.open(selected.sources[0].id, selected.profiles[0].id).sessionId;
                const options = { width: Math.floor(settings.width), height: Math.floor(settings.height), maxFps: Math.floor(settings.maxFps) };
                const subscription = ajn.frames.subscribe(id, options).subscriptionId;
                sessions.push({ id, subscription, received: 0, last: null, error: null });
            } catch (failure) {
                if (id) { try { ajn.sessions.requestClose(id); } catch (_) {} }
                error = failure.code || String(failure.message); break;
            }
        }
        if (sessions.length) ajn.timers.set("samples", Math.max(16, Math.round(1000 / settings.maxFps)));
        return { opened: sessions.length, error };
    }
    if (event.data.id === "status") return sessions.map(item => ({ sessionId: item.id, received: item.received, last: item.last, error: item.error }));
}
```

<a id="example-player-inspector"></a>

### player-inspector

**manifest.json**

<!-- release-example:player-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.player-inspector",
  "name": "Player sample inspector",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 6 },
  "moduleSha256": "0000000000000000000000000000000000000000000000000000000000000000",
  "permissions": ["player.observe", "frames.read"],
  "requiredCapabilities": { "playerFrames": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "playerNumber": { "type": "number", "label": "Player number from the list", "default": 1, "minimum": 1, "maximum": 4 },
    "width": { "type": "number", "label": "Sample width", "default": 64, "minimum": 1, "maximum": 320 },
    "height": { "type": "number", "label": "Sample height", "default": 36, "minimum": 1, "maximum": 180 },
    "fps": { "type": "number", "label": "Maximum samples per second", "default": 30, "minimum": 1, "maximum": 60 }
  },
  "actions": {
    "list": { "label": "List attached players" },
    "subscribe": { "label": "Observe selected player" },
    "latest": { "label": "Show latest sample" },
    "unsubscribe": { "label": "Stop observing" }
  }
}
```

**addon.js**

<!-- release-example:player-inspector/addon.js -->
```javascript
// Generic framework inspector: no network traffic, playback controls or device
// integrations. Keep the addon running between subscribe and latest actions.
let subscription = null;
function onEvent(event, ajn) {
    if (event.name === "stop") {
        if (subscription) {
            try { ajn.playerFrames.unsubscribe(subscription); } catch (_) {}
            subscription = null;
        }
        return;
    }
    if (event.name !== "action") return;
    const action = event.data.id;
    try {
        if (action === "list") return ajn.playerFrames.list().map((player, index) => Object.assign({number: index + 1}, player));
        if (action === "unsubscribe") {
            if (subscription) ajn.playerFrames.unsubscribe(subscription);
            subscription = null;
            return {observing: false};
        }
        if (action === "subscribe") {
            if (subscription) ajn.playerFrames.unsubscribe(subscription);
            subscription = null;
            const settings = ajn.settings.get();
            const player = ajn.playerFrames.list()[Math.floor(settings.playerNumber) - 1];
            if (!player) return {error: "The selected player is not attached."};
            const selected = ajn.playerFrames.subscribe(player.playerId, {width: Math.floor(settings.width), height: Math.floor(settings.height), maxFps: Math.floor(settings.fps)});
            subscription = selected.subscriptionId;
            return selected;
        }
        if (action === "latest") {
            if (!subscription) return {error: "Choose Observe selected player first."};
            const frame = ajn.playerFrames.read(subscription);
            if (!frame) return {frame: null};
            let checksum = 0;
            for (const byte of frame.pixels) checksum = (checksum + byte) >>> 0;
            return {frameId: frame.frameId, epoch: frame.epoch, ptsSeconds: frame.ptsSeconds,
                width: frame.width, height: frame.height, sourceWidth: frame.sourceWidth, sourceHeight: frame.sourceHeight,
                color: frame.color, stage: frame.stage, bytes: frame.pixels.length, checksum};
        }
    } catch (error) {
        return {error: error.code || "sample_unavailable", message: error.message};
    }
}
```

<a id="example-service-inspector"></a>

### service-inspector

**manifest.json**

<!-- release-example:service-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.service-inspector",
  "name": "Service inspector example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 3 },
  "permissions": ["network.connect", "credentials.use"],
  "requiredCapabilities": { "network": { "major": 1, "minMinor": 0 }, "timers": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "destinationIndex": { "type": "number", "label": "Approved destination number", "description": "1 selects the first destination shown by List approved services.", "default": 1, "minimum": 1, "maximum": 8 },
    "path": { "type": "string", "label": "HTTP request path", "default": "/", "maxLength": 2048 },
    "useCredential": { "type": "boolean", "label": "Use this service's saved credential", "default": false },
    "datagram": { "type": "string", "label": "UDP message", "description": "Sent only when you choose Send request for a UDP destination.", "default": "AJN service test", "maxLength": 1024 }
  },
  "actions": {
    "list": { "label": "List approved services" },
    "request": { "label": "Send request", "description": "Start the addon first. Sends one HTTP GET or the configured UDP message to the selected destination." },
    "status": { "label": "Show request result" },
    "cancel": { "label": "Cancel request" }
  }
}
```

**addon.js**

<!-- release-example:service-inspector/addon.js -->
```javascript
// Generic broker example. All destinations come from explicit host consent.
let requestId = null, last = { message: "No request sent." };
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Ready. Approve a service, start this addon, then choose Send request." };
    if (event.name === "stop") { if (requestId) ajn.network.cancel(requestId); return; }
    if (event.name === "timer" && event.data.timerId === "request") {
        try {
            const response = ajn.network.result(requestId);
            if (response.state === "pending") return;
            last = response.state === "failed" ? response.error : {
                state: response.state, status: response.status, bytes: response.body.length,
                contentType: response.headers["content-type"] || null,
            };
            requestId = null; ajn.timers.clear("request");
        } catch (error) {
            if (error.code === "bandwidth_exceeded") return;
            last = { error: error.code || "request_failed", message: error.message };
            if (requestId) { try { ajn.network.cancel(requestId); } catch (_) {} }
            requestId = null; ajn.timers.clear("request");
        }
        return;
    }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "list") return ajn.network.selections();
        if (event.data.id === "status") return last;
        if (event.data.id === "cancel") {
            if (requestId) { ajn.network.cancel(requestId); last = { state: "cancelling" }; }
            return last;
        }
        if (event.data.id === "request") {
            if (requestId) return { message: "A request is already active. Wait for it or cancel it." };
            const settings = ajn.settings.get();
            const destinations = ajn.network.selections().destinations;
            const destination = destinations[Math.floor(settings.destinationIndex) - 1];
            if (!destination) return { message: "Approve and select a service first." };
            if (destination.protocol === "udp") return last = ajn.network.sendDatagram(destination.id, settings.datagram);
            requestId = ajn.network.request(destination.id, { path: settings.path, useCredential: settings.useCredential }).requestId;
            ajn.timers.set("request", 100); return last = { state: "pending" };
        }
    } catch (error) { return last = { error: error.code || "request_failed", message: error.message }; }
}
```

<a id="example-remote-inspector"></a>

### remote-inspector

**manifest.json**

<!-- release-example:remote-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.remote-inspector",
  "name": "Remote media inspector example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 5 },
  "permissions": ["sessions.manage", "media.input", "media.output", "network.connect", "credentials.use"],
  "requiredCapabilities": { "sessions": { "major": 1, "minMinor": 1 }, "remoteSources": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "sourceIndex": { "type": "number", "label": "Approved source service number", "default": 1, "minimum": 1, "maximum": 8 },
    "profileIndex": { "type": "number", "label": "Approved profile number", "default": 1, "minimum": 1, "maximum": 8 },
    "sourcePath": { "type": "string", "label": "Media path on source service", "description": "A single media resource, such as /videos/sample.mp4. Playlists and redirects are not supported.", "default": "/media", "maxLength": 2048 },
    "useSourceCredential": { "type": "boolean", "label": "Use source service's saved credential", "default": false },
    "sessionCount": { "type": "number", "label": "Number of independent sessions", "description": "Each session processes the source separately. Host capacity and hardware limit admission.", "default": 1, "minimum": 1, "maximum": 16 },
    "seekSeconds": { "type": "number", "label": "Seek position (seconds)", "default": 10, "minimum": 0, "maximum": 86400 },
    "destinationIndex": { "type": "number", "label": "Approved output service number", "description": "Used only for Send processed media. Numbers follow List approved resources.", "default": 1, "minimum": 1, "maximum": 8 },
    "destinationPath": { "type": "string", "label": "Output upload path", "default": "/upload", "maxLength": 2048 },
    "useDestinationCredential": { "type": "boolean", "label": "Use output service's saved credential", "default": false },
    "lengthSeconds": { "type": "number", "label": "Encoded output duration (seconds)", "description": "Used only for Send processed media. Zero runs to source EOF within host limits.", "default": 2, "minimum": 0, "maximum": 86400 }
  },
  "actions": {
    "resources": { "label": "List approved resources" },
    "formats": { "label": "Show remote media limits" },
    "open": { "label": "Process remote media", "description": "Reads media from the selected service into independent AJN processing sessions." },
    "send": { "label": "Send processed media", "description": "Also requires media output permission and a supported NVIDIA GPU. Sends H.264/AAC Matroska to the selected receiver." },
    "status": { "label": "Show session status" },
    "pause": { "label": "Pause sessions" },
    "resume": { "label": "Resume sessions" },
    "seek": { "label": "Seek sessions" },
    "close": { "label": "Close sessions" }
  }
}
```

**addon.js**

<!-- release-example:remote-inspector/addon.js -->
```javascript
// Generic remote-media example. Startup performs no network or GPU work.
let sessions = [];
function onEvent(event, ajn) {
    function status() {
        const results = [];
        sessions = sessions.filter(id => {
            try { results.push({ id, ...ajn.sessions.status(id) }); return true; }
            catch (error) { if (error.code === "session_not_found") return false; throw error; }
        });
        return results;
    }
    function close() {
        status();
        for (const id of sessions) ajn.sessions.requestClose(id);
        return { message: "Cleanup requested. Check status before opening more sessions." };
    }
    if (event.name === "start") return { message: "Ready. Approve a media service and processing profile, then choose Process remote media." };
    if (event.name === "stop") { close(); return; }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "resources") return { ...ajn.sessions.selections(), ...ajn.network.selections() };
        if (event.data.id === "formats") return ajn.remoteSources.formats();
        if (event.data.id === "status") return status();
        if (event.data.id === "close") return close();
        if (["pause", "resume", "seek"].includes(event.data.id)) {
            const current = status(), settings = ajn.settings.get();
            if (event.data.id === "seek" && current.some(s => s.input?.seekable !== true || s.output))
                return { message: "All sessions must be seekable and have no encoded output. Close and reopen outputs to restart them." };
            for (const id of sessions) {
                if (event.data.id === "seek") ajn.sessions.seek(id, settings.seekSeconds);
                else ajn.sessions.pause(id, event.data.id === "pause");
            }
            return { message: "Control requested. Check session status." };
        }
        if (event.data.id === "open" || event.data.id === "send") {
            status();
            if (sessions.length) return { message: "Close existing sessions first, including completed sessions.", sessions };
            const settings = ajn.settings.get();
            for (const key of ["sourceIndex", "profileIndex", "destinationIndex", "sessionCount"])
                if (!Number.isInteger(settings[key])) return { message: "Choose a whole number for " + key + "." };
            const profile = ajn.sessions.selections().profiles[settings.profileIndex - 1];
            const choices = ajn.network.selections().destinations;
            const source = choices[settings.sourceIndex - 1], destination = choices[settings.destinationIndex - 1];
            if (!source || !profile || !["http", "https"].includes(source.protocol))
                return { message: "Approve and select an HTTP media service and processing profile first." };
            if (event.data.id === "send" && (!destination || !["http", "https"].includes(destination.protocol)))
                return { message: "Approve and select an HTTP upload receiver first." };
            const input = { destinationId: source.id, path: settings.sourcePath, useCredential: settings.useSourceCredential };
            for (let i = 0; i < settings.sessionCount; i++) {
                const opened = event.data.id === "open" ? ajn.sessions.openRemote(input, profile.id) :
                    ajn.outputs.openRemote(input, profile.id, {
                        encoding: { videoCodec: "h264", container: "matroska", videoKbps: 4000,
                            audioCodec: "aac", lengthSeconds: settings.lengthSeconds },
                        destination: { destinationId: destination.id, path: settings.destinationPath,
                            useCredential: settings.useDestinationCredential },
                    });
                sessions.push(opened.sessionId);
            }
            return { message: "Sessions are initializing. Check status for media and processing results.", sessions };
        }
    } catch (error) { return { error: error.code || "input_failed", message: error.message, sessions }; }
}
```

<a id="example-output-inspector"></a>

### output-inspector

**manifest.json**

<!-- release-example:output-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.output-inspector",
  "name": "Media output inspector example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 4 },
  "permissions": ["sessions.manage", "media.output", "network.connect", "credentials.use"],
  "requiredCapabilities": { "sessions": { "major": 1, "minMinor": 1 }, "outputs": { "major": 1, "minMinor": 0 }, "network": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "sourceIndex": { "type": "number", "label": "Approved media file number", "default": 1, "minimum": 1, "maximum": 16 },
    "profileIndex": { "type": "number", "label": "Approved profile number", "default": 1, "minimum": 1, "maximum": 8 },
    "destinationIndex": { "type": "number", "label": "Approved service number", "description": "Numbers follow List approved resources. Select an HTTP or HTTPS receiver that accepts a streamed POST or PUT.", "default": 1, "minimum": 1, "maximum": 8 },
    "sessionCount": { "type": "number", "label": "Number of independent outputs", "description": "Each output processes the selected source separately. Host capacity and hardware limit admission.", "default": 1, "minimum": 1, "maximum": 16 },
    "videoCodec": { "type": "choice", "label": "Video codec", "choices": ["h264", "hevc", "av1"], "default": "h264" },
    "container": { "type": "choice", "label": "Container", "choices": ["matroska", "mpegts", "fragmentedMp4"], "default": "matroska" },
    "videoKbps": { "type": "number", "label": "Video bitrate (kbps)", "default": 4000, "minimum": 256, "maximum": 50000 },
    "audioCodec": { "type": "choice", "label": "Audio codec", "choices": ["none", "aac", "opus"], "default": "aac" },
    "lengthSeconds": { "type": "number", "label": "Output duration (seconds)", "description": "The example sends a short clip by default. Zero processes to the end of the source, within host limits.", "default": 2, "minimum": 0, "maximum": 86400 },
    "method": { "type": "choice", "label": "HTTP method", "choices": ["POST", "PUT"], "default": "POST" },
    "path": { "type": "string", "label": "Service upload path", "default": "/media", "maxLength": 2048 },
    "useCredential": { "type": "boolean", "label": "Use this service's saved credential", "default": false }
  },
  "actions": {
    "resources": { "label": "List approved resources" },
    "formats": { "label": "Show host output formats" },
    "open": { "label": "Send processed media", "description": "Starts the configured outputs and sends video and optional audio to the selected approved service." },
    "status": { "label": "Show output status" },
    "pause": { "label": "Pause outputs" },
    "resume": { "label": "Resume outputs" },
    "close": { "label": "Close outputs" }
  }
}
```

**addon.js**

<!-- release-example:output-inspector/addon.js -->
```javascript
// General output API example. Starting it never sends media automatically.
// Only approved opaque resource IDs reach the SDK, never paths or raw handles.
let sessions = [];
function onEvent(event, ajn) {
    function status() {
        const results = [];
        sessions = sessions.filter(id => {
            try { results.push({ id, ...ajn.sessions.status(id) }); return true; }
            catch (error) { if (error.code === "session_not_found") return false; throw error; }
        });
        return results;
    }
    function close() {
        status();
        for (const id of sessions) ajn.sessions.requestClose(id);
        return { message: "Cleanup requested. Show output status to check; close again if cleanup needs a retry." };
    }
    if (event.name === "start") return { message: "Ready. Approve a file, profile and receiver, then choose Send processed media." };
    if (event.name === "stop") { close(); return; }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "resources") return { ...ajn.sessions.selections(), ...ajn.network.selections() };
        if (event.data.id === "formats") return ajn.outputs.formats();
        if (event.data.id === "status") return status();
        if (event.data.id === "close") return close();
        if (event.data.id === "pause" || event.data.id === "resume") {
            for (const id of sessions) ajn.sessions.pause(id, event.data.id === "pause");
            return { message: "Control requested. Check output status." };
        }
        if (event.data.id === "open") {
            status();
            if (sessions.length) return { message: "Close existing outputs first, including completed outputs.", sessions };
            const settings = ajn.settings.get();
            for (const key of ["sourceIndex", "profileIndex", "destinationIndex", "sessionCount", "videoKbps"])
                if (!Number.isInteger(settings[key])) return { message: "Choose a whole number for " + key + "." };
            const selected = ajn.sessions.selections();
            const source = selected.sources[settings.sourceIndex - 1], profile = selected.profiles[settings.profileIndex - 1];
            const destination = ajn.network.selections().destinations[settings.destinationIndex - 1];
            if (!source || !profile || !destination) return { message: "Approve and select a file, profile and receiver first." };
            if (destination.protocol !== "http" && destination.protocol !== "https") return { message: "Select an HTTP or HTTPS upload receiver." };
            for (let i = 0; i < settings.sessionCount; i++) {
                const opened = ajn.outputs.open(source.id, profile.id, {
                    encoding: { videoCodec: settings.videoCodec, container: settings.container, videoKbps: settings.videoKbps,
                        audioCodec: settings.audioCodec, lengthSeconds: settings.lengthSeconds },
                    destination: { destinationId: destination.id, method: settings.method, path: settings.path, useCredential: settings.useCredential },
                });
                sessions.push(opened.sessionId);
            }
            return { message: "Outputs are initializing. Show output status for delivery and processing results.", sessions };
        }
    } catch (error) { return { error: error.code || "output_failed", message: error.message, sessions }; }
}
```

<a id="example-http-inspector"></a>

### http-inspector

**manifest.json**

<!-- release-example:http-inspector/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.http-inspector",
  "name": "Local HTTP listener example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 7 },
  "permissions": ["network.listen"],
  "requiredCapabilities": { "httpServer": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "actions": {
    "open": { "label": "Open approved listener" },
    "status": { "label": "Show listener status" },
    "close": { "label": "Close listener" }
  }
}
```

**addon.js**

<!-- release-example:http-inspector/addon.js -->
```javascript
// A loopback diagnostic, not authentication or a media streaming application.
// Request metadata stays out of logs and action results.
let serverId = null;
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Approve a local listener, then choose Open approved listener." };
    if (event.name === "stop") { if (serverId) ajn.httpServer.requestClose(serverId); return; }
    if (event.name === "http.request") {
        const allowed = event.data.method === "GET" || event.data.method === "HEAD";
        try { ajn.httpServer.respond(event.data.requestId, {
            status: allowed ? 200 : 405,
            headers: [{ name: "Content-Type", values: ["text/plain; charset=utf-8"] }],
            body: allowed ? "Hello from an AJN addon." : "Use GET or HEAD.",
        }); } catch (error) {
            // Disconnect/expiry can race event delivery. They do not make the
            // listener unusable for the next client.
            if (error.code !== "request_not_found") throw error;
        }
        return;
    }
    if (event.name !== "action") return;
    try {
        if (event.data.id === "open") {
            if (serverId) return { message: "A listener is already open or closing." };
            const choices = ajn.httpServer.selections().listeners;
            if (!choices.length) return { message: "Approve a loopback listener in Listening access first." };
            serverId = ajn.httpServer.open(choices[0].id).serverId;
            return { message: "Opening listener. Use Show listener status." };
        }
        if (!serverId) return { message: "No listener open." };
        if (event.data.id === "close") { ajn.httpServer.requestClose(serverId); return { message: "Closing. Check status before opening again." }; }
        if (event.data.id === "status") return ajn.httpServer.status(serverId);
    } catch (error) {
        if (error.code === "server_not_found") { serverId = null; return { message: "Listener closed." }; }
        return { message: error.message, code: error.code };
    }
}
```

<a id="example-http-bridge"></a>

### http-bridge

**manifest.json**

<!-- release-example:http-bridge/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.http-bridge-example",
  "name": "HTTP bridge developer example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 7 },
  "permissions": ["network.listen", "network.connect", "network.proxy", "credentials.use", "credentials.delegate"],
  "sensitiveRequestFields": { "headers": ["X-Client-Key"], "query": [] },
  "requiredCapabilities": {
    "httpServer": { "major": 1, "minMinor": 0 },
    "httpProxy": { "major": 1, "minMinor": 0 },
    "requestCredentials": { "major": 1, "minMinor": 0 }
  },
  "activation": ["manual"],
  "actions": {
    "open": { "label": "Open approved loopback bridge" },
    "status": { "label": "Show bridge status" },
    "close": { "label": "Close bridge" }
  }
}
```

**addon.js**

<!-- release-example:http-bridge/addon.js -->
```javascript
// Diagnostic only. This example deliberately accepts only a loopback listener.
// Real integrations must authenticate clients before proxying or serving media.
let serverId = null, destinationId = null;
const jobs = [];
const operations = [];
const credentials = new Map();
function onEvent(event, ajn) {
    if (event.name === "start") return { message: "Approve a loopback listener and HTTP service, then open the bridge." };
    if (event.name === "stop") {
        if (serverId) { const closing = serverId; serverId = null; ajn.httpServer.requestClose(closing); }
        ajn.timers.clear("poll"); return;
    }
    if (event.name === "action") {
        if (event.data.id === "open") {
            if (serverId) return { message: "Bridge already open or closing." };
            const listener = ajn.httpServer.selections().listeners.find(l => l.binding.scope === "loopback");
            const destination = ajn.network.selections().destinations.find(d => d.protocol === "http" || d.protocol === "https");
            if (!listener || !destination) return { message: "Approve a loopback listener and one HTTP service first." };
            destinationId = destination.id;
            serverId = ajn.httpServer.open(listener.id).serverId;
            ajn.timers.set("poll", 100);
        }
        if (event.data.id === "close" && serverId) { const closing = serverId; serverId = null; ajn.httpServer.requestClose(closing); }
        if (!serverId) return { state: "closed" };
        try { return ajn.httpServer.status(serverId); }
        catch (error) { if (error.code !== "server_not_found") throw error; serverId = null; return { state: "closed" }; }
    }
    if (event.name === "http.request") {
        const r = event.data;
        try {
            if (r.path === "/authorized-proxy") {
                // Example upstream contract: /account returns HTTP 200 and {allowed:true}.
                // Replace that validation with the target service's actual authorization policy.
                const context = ajn.requestCredentials.capture(r.requestId, destinationId, [
                    { from: "header", name: "X-Client-Key", to: "header", target: "X-Upstream-Key" }
                ], 120);
                try {
                    ajn.httpServer.extend(r.requestId, 40);
                    const operationId = ajn.httpProxy.buffer(destinationId, { path: "/account", credentialId: context.credentialId, maximumBytes: 32768 }).operationId;
                    operations.push(operationId); jobs.push({ requestId: r.requestId, operationId, authorize: true, credentialId: context.credentialId });
                } catch (error) { ajn.requestCredentials.release(context.credentialId); throw error; }
            } else if (r.path === "/control" && r.method === "POST") {
                ajn.httpServer.readBody(r.requestId);
                jobs.push({ requestId: r.requestId });
            } else if (r.path === "/proxy") {
                operations.push(ajn.httpProxy.forward(r.requestId, destinationId, { path: "/" }).operationId);
            } else if (r.path === "/metadata" && r.method === "GET") {
                ajn.httpServer.extend(r.requestId, 40);
                const operationId = ajn.httpProxy.buffer(destinationId, { path: "/", maximumBytes: 262144 }).operationId;
                operations.push(operationId);
                jobs.push({ requestId: r.requestId, operationId });
            } else ajn.httpServer.respond(r.requestId, { status: 404, body: "Use POST /control, /proxy or GET /metadata." });
        } catch (error) {
            if (error.code === "request_not_found") return;
            try { ajn.httpServer.respond(r.requestId, { status: error.code === "credential_field_missing" ? 401 : 503, body: "Operation unavailable or client credential missing." }); }
            catch (closing) { if (!["request_not_found", "request_claimed"].includes(closing.code)) throw closing; }
        }
        return;
    }
    if (event.name !== "timer" || event.data.timerId !== "poll") return;
    // Limit work per callback; clients waiting for I/O never block the Wasm gate.
    for (let budget = Math.min(2, jobs.length); budget > 0; budget--) {
        const job = jobs.shift();
        try {
            const status = job.operationId ? ajn.httpProxy.status(job.operationId) : ajn.httpServer.requestStatus(job.requestId);
            if (job.operationId ? status.state === "pending" : status.bodyState === "reading") { jobs.push(job); continue; }
            const failed = job.operationId ? status.state === "failed" : status.bodyState === "failed";
            if (failed) ajn.httpServer.respond(job.requestId, { status: 502, body: "Body could not be read." });
            else if (job.authorize) {
                let allowed = false;
                try {
                    if (status.status === 200) allowed = JSON.parse(new TextDecoder().decode(ajn.httpProxy.read(job.operationId, 0).body)).allowed === true;
                } catch (_) { /* Invalid upstream authorization data denies access. */ }
                if (!allowed) ajn.httpServer.respond(job.requestId, { status: 403, body: "Client is not authorized." });
                else {
                    const forwarded = ajn.httpProxy.forward(job.requestId, destinationId, { path: "/", credentialId: job.credentialId }).operationId;
                    operations.push(forwarded); credentials.set(forwarded, job.credentialId); job.credentialId = null;
                }
            } else {
                ajn.httpServer.beginResponse(job.requestId, { status: job.operationId ? status.status : 200 });
                for (let offset = 0; offset < status.bodyLength; offset += 32768) {
                    const chunk = job.operationId ? ajn.httpProxy.read(job.operationId, offset) : ajn.httpServer.bodyChunk(job.requestId, offset);
                    ajn.httpServer.appendResponse(job.requestId, chunk.body);
                }
                ajn.httpServer.finishResponse(job.requestId);
            }
        } catch (error) {
            if (job.operationId) { try { ajn.httpProxy.cancel(job.operationId); } catch (_) {} }
            if (!["request_not_found", "operation_not_found"].includes(error.code)) throw error;
        } finally {
            // Pending validation retains its context; transfer completion releases it later.
            if (job.credentialId && !jobs.includes(job)) ajn.requestCredentials.release(job.credentialId);
        }
    }
    for (let budget = Math.min(4, operations.length); budget > 0; budget--) {
        const id = operations.shift();
        if (!jobs.some(j => j.operationId === id) && ajn.httpProxy.status(id).cleanupReady) {
            ajn.httpProxy.close(id);
            if (credentials.has(id)) { ajn.requestCredentials.release(credentials.get(id)); credentials.delete(id); }
        } else operations.push(id);
    }
}
```

<a id="example-media-stream"></a>

### media-stream

**manifest.json**

<!-- release-example:media-stream/manifest.json -->
```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.media-stream-example",
  "name": "Local streaming developer example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 7 },
  "permissions": ["sessions.manage", "media.input", "media.output", "network.listen", "network.connect", "credentials.use"],
  "requiredCapabilities": { "mediaStreams": { "major": 1, "minMinor": 0 }, "httpServer": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "sourceIndex": { "type": "number", "label": "Approved source number", "default": 1, "minimum": 1, "maximum": 16 },
    "profileIndex": { "type": "number", "label": "Approved profile number", "default": 1, "minimum": 1, "maximum": 8 },
    "remote": { "type": "boolean", "label": "Read from first approved HTTP service", "default": false },
    "path": { "type": "string", "label": "Remote media path", "default": "/media", "maxLength": 2048 },
    "useCredential": { "type": "boolean", "label": "Use the service's saved credential", "default": false },
    "container": { "type": "choice", "label": "Output container", "choices": ["mpegts", "fragmentedMp4", "matroska"], "default": "mpegts" },
    "mode": { "type": "choice", "label": "Delivery mode", "choices": ["segments", "continuous"], "default": "segments" },
    "segmentSeconds": { "type": "number", "label": "Target segment duration in seconds", "default": 1, "minimum": 0.5, "maximum": 6 },
    "startSeconds": { "type": "number", "label": "Start position in source seconds", "default": 0, "minimum": 0, "maximum": 86400 },
    "lengthSeconds": { "type": "number", "label": "Output duration (zero means to end)", "default": 10, "minimum": 0, "maximum": 86400 },
    "audioTrackId": { "type": "string", "label": "Probe audio track ID (empty means default)", "default": "", "maxLength": 68 },
    "subtitleTrackId": { "type": "string", "label": "Probe subtitle track ID to burn (empty means none)", "default": "", "maxLength": 68 }
  },
  "actions": {
    "resources": { "label": "Show approved resources" },
    "open": { "label": "Open loopback listener" },
    "probe": { "label": "Probe selected source" },
    "probeStatus": { "label": "Show probe and track IDs" },
    "play": { "label": "Start selected stream" },
    "replace": { "label": "Replace playback with current settings" },
    "pause": { "label": "Pause stream" },
    "resume": { "label": "Resume stream" },
    "status": { "label": "Show playback and listener status" },
    "close": { "label": "Close stream and listener" }
  }
}
```

**addon.js**

<!-- release-example:media-stream/addon.js -->
```javascript
// Loopback diagnostic. Any local client can read the selected media while open.
// Production integrations must authenticate/authorize EACH request before serve.
// All encoding, files and media bytes stay in AJN. Callbacks never wait for media.
let server = null, stream = null, probe = null, replacement = null, listener = null;
function onEvent(event, ajn) {
    function selection() {
        const s = ajn.settings.get(), choices = ajn.sessions.selections();
        if (!Number.isInteger(s.sourceIndex) || !Number.isInteger(s.profileIndex)) throw new Error("Choose whole resource numbers.");
        const profile = choices.profiles[s.profileIndex - 1];
        if (!profile) throw new Error("Approve and select a profile first.");
        let source;
        if (s.remote) {
            const upstream = ajn.network.selections().destinations.find(d => d.protocol === "http" || d.protocol === "https");
            if (!upstream) throw new Error("Approve an HTTP media service first.");
            source = { type: "http", destinationId: upstream.id, path: s.path, useCredential: s.useCredential };
        } else {
            const selected = choices.sources[s.sourceIndex - 1];
            if (!selected) throw new Error("Approve and select a source first.");
            source = { type: "local", sourceId: selected.id };
        }
        return { source, profileId: profile.id, options: {
            encoding: { videoCodec: "h264", container: s.container, videoKbps: 4000, audioCodec: "aac", audioChannels: 2, lengthSeconds: s.lengthSeconds },
            mode: s.mode, segmentSeconds: s.segmentSeconds,
            playback: { startSeconds: s.startSeconds, audioTrack: s.audioTrackId ? { trackId: s.audioTrackId } : "default",
                subtitles: s.subtitleTrackId ? { mode: "burn", trackId: s.subtitleTrackId } : { mode: "none" } },
        } };
    }
    function begin(plan) {
        if (!server || ajn.httpServer.status(server).state !== "listening") throw new Error("Open the loopback listener first.");
        stream = Object.assign(ajn.mediaStreams.open(plan.source, plan.profileId, plan.options), { container: plan.options.encoding.container });
    }
    function status() {
        return { listener: server ? ajn.httpServer.status(server) : null,
            stream: stream ? ajn.mediaStreams.status(stream.streamId) : null, replacing: replacement !== null };
    }
    function reply(id, code, value, type = "application/json") {
        ajn.httpServer.respond(id, { status: code, headers: [{ name: "Content-Type", values: [type] }], body: typeof value === "string" ? value : JSON.stringify(value) });
    }
    if (event.name === "start") return { message: "Approve a loopback listener, media source and DirectML profile. Open listener, then start a stream." };
    if (event.name === "stop") {
        replacement = null; ajn.timers.clear("poll");
        if (stream) ajn.mediaStreams.requestClose(stream.streamId);
        if (server) { const closing = server; server = null; ajn.httpServer.requestClose(closing); }
        if (probe) ajn.mediaProbe.cancel(probe);
        return;
    }
    if (event.name === "http.closed" && !event.data.requestId && event.data.serverId === server) { server = null; return; }
    if (event.name === "action") {
        try {
            switch (event.data.id) {
                case "resources": return { ...ajn.sessions.selections(), ...ajn.httpServer.selections() };
                case "open":
                    if (!server) {
                        listener = ajn.httpServer.selections().listeners.find(l => l.binding.scope === "loopback");
                        if (!listener) return { message: "Approve a loopback listener first. This example refuses LAN/public bindings." };
                        server = ajn.httpServer.open(listener.id).serverId; ajn.timers.set("poll", 100);
                    }
                    return status();
                case "probe":
                    if (probe) { ajn.mediaProbe.close(probe); probe = null; }
                    probe = ajn.mediaProbe.open(selection().source).probeId; return { probeId: probe };
                case "probeStatus":
                    if (!probe) return { message: "Probe the selected source first." };
                    const p = ajn.mediaProbe.status(probe); return p.state === "completed" ? ajn.mediaProbe.result(probe) : p;
                case "play":
                    if (stream) return { message: "Close or replace the current stream first." };
                    begin(selection()); return status();
                case "replace":
                    replacement = selection();
                    if (stream) ajn.mediaStreams.requestClose(stream.streamId);
                    else { begin(replacement); replacement = null; }
                    return status();
                case "pause": case "resume":
                    if (stream) ajn.mediaStreams.pause(stream.streamId, event.data.id === "pause"); return status();
                case "close":
                    replacement = null;
                    if (stream) ajn.mediaStreams.requestClose(stream.streamId);
                    if (server) { const closing = server; server = null; ajn.httpServer.requestClose(closing); }
                    return status();
                default: return status();
            }
        } catch (error) { return { error: error.code || "example_setup", message: error.message }; }
    }
    if (event.name === "timer" && event.data.timerId === "poll") {
        if (!stream) return;
        const state = ajn.mediaStreams.status(stream.streamId);
        if ((state.state === "closed" || state.state === "failed") && state.nativeCapacityReleased) {
            stream = null;
            if (replacement) { const plan = replacement; replacement = null; begin(plan); }
        }
        // A client supplies actual playback demand; downloading segments is not viewing.
        return;
    }
    if (event.name !== "http.request") return;
    const r = event.data;
    try {
        // Deliberate local-only example authorization, checked for every route.
        if (r.serverId !== server || !listener || listener.binding.scope !== "loopback") { reply(r.requestId, 403, "Denied"); return; }
        if (r.path === "/status") { reply(r.requestId, 200, status()); return; }
        if (!stream) { reply(r.requestId, 404, "No stream"); return; }
        const id = stream.streamId;
        if (r.path === "/segments") {
            const cursor = r.query.find(q => q.name === "cursor");
            reply(r.requestId, 200, ajn.mediaStreams.segments(id, cursor ? cursor.values[0] : null)); return;
        }
        if (r.path === "/demand" && r.method === "POST") {
            const position = r.query.find(q => q.name === "seconds"), generation = r.query.find(q => q.name === "generation");
            if (!generation || generation.values[0] !== stream.generationId || !position || position.values.length !== 1) { reply(r.requestId, 409, "Expected current generation and position"); return; }
            const seconds = Number(position.values[0]);
            if (!Number.isFinite(seconds)) { reply(r.requestId, 400, "Invalid position"); return; }
            ajn.mediaStreams.setDemand(id, seconds); reply(r.requestId, 200, { accepted: true }); return;
        }
        const media = /^\/media\/([a-f0-9]{32})\/([a-f0-9]{32})$/.exec(r.path);
        if (media) { ajn.mediaStreams.serve(r.requestId, id, media[1], media[2]); return; }
        reply(r.requestId, 404, "Use /status, /segments, POST /demand or a generation-scoped /media route.");
    } catch (error) {
        try { reply(r.requestId, ["stale_generation", "segment_expired", "stream_replacement_required"].includes(error.code) ? 409 : 503, { error: error.code || "stream_unavailable" }); }
        catch (closed) { if (!["request_not_found", "request_claimed"].includes(closed.code)) throw closed; }
    }
}
```


<a id="provenance"></a>

## Appendix C. Release compatibility and documentation provenance

This edition documents API 1.8 development. The packaged `build-info/addon-preview.json`,
`build-info/host-build.json` and `build-info/inference-build.json` record the exact
source revisions, input hashes and test reports for the supplied binaries.
The standalone host bundle has `host-build.json` at its root. This document is
self-contained for the public API and examples; those records establish binary
provenance, not additional instructions required to implement an addon.

The updated implementation is maintained in the `integration/addons` branches
of the maintainer forks, plus `integration/addon-scene-detection` in inference.
This guide does not assert that an upstream release has adopted these changes.
Use the guide supplied with the build and negotiate optional capabilities.

<a id="scene-detection"></a>

## Appendix D. Custom RIFE scene detection

This optional API lets a WebAssembly addon decide whether RIFE should interpolate
between a pair of frames. Creators can port motion-aware detectors to Wasm.
The `scene-detector` example demonstrates the contract using average luma
difference. It is **not an MVTools port** and does not reproduce `sc_mode=2`.

## Requirements and installation

Use a Windows x64 API 1.8 host, matching player with `privateSceneAbi: 1`, and
inference dispatcher/backend implementing the optional scene ABI. Enable RIFE
in the selected AJN profile first. This API does not enable RIFE or select/build
its model. It currently targets ordinary AJN players, not independent headless
addon sessions. Samples support progressive mono SDR; other inputs fall back.
CUDA and D3D11 hardware downloads are implemented; consult the build's test
results for which backend/hardware combinations have been qualified.

Build the example with the ordinary AJN build command. Install its `.ajnaddon`
in Manager's Addons page and approve `player.sceneDetection` and `frames.read`.
Start RIFE playback and the addon, then use **Enable on available AJN players**.
Use **Show detector status** to check progress. **Restore AJN scene detection**,
disabling the addon or closing its worker releases its attachments. Installation
alone does not change playback: the example uses manual activation. No remote
host address is needed. A replacement player gets a new ID; attach again.

The example's **Developer test mode** offers `cut`, `continuous` and `default`
for connection tests; `analyze` runs its demonstration algorithm. Suspension
requires detach/attach. A production addon may manage player discovery with
bounded timers and reset its own history when playback changes.

## Manifest and compatibility

Declare `api: {"major":1,"minMinor":8}`, permissions
`["player.sceneDetection","frames.read"]`, and
`requiredCapabilities: {"sceneDetection":{"major":1,"minMinor":0}}`.
The package remains `manifest.json` plus `module.wasm`. Ported C/C++ code must
produce compatible core Wasm and implement the same RPC protocol. Native DLLs
are not accepted package contents. `player.observe` does not grant this control.

The host advertises this capability only with matching native player metadata.
An older inference backend reports `backendUnavailable` and retains AJN's
detector. Existing addon calls, inference struct layouts and `aji_infer_rife`
callers keep their behavior; this is an additive optional API.

## JavaScript contract

| Call | Result and limits |
| --- | --- |
| `ajn.sceneDetection.list()` | `{playerId,inUse,format:"gray8",stage:"beforeInterpolation"}[]`. Same opaque player IDs as normal observations; no titles/paths. |
| `attach(playerId,{width?,height?,deadlineMs?})` | `{detectorId,width,height,deadlineMs,format,stage}`. Defaults 160×90, 25 ms; width 1–320, height 1–180, deadline 5–100 ms. |
| `read(detectorId)` | Latest pending pair once, or `null` when consumed, expired or unavailable. |
| `submit(detectorId,requestId,decision)` | `{accepted:boolean}`; decision is `cut`, `continuous` or `default`. |
| `status(detectorId)` | `{state,acceptedPairs,timedOutPairs}`. |
| `detach(detectorId)` | Releases the caller's detector and restores built-in detection. |

There is one exclusive detector per player, at most four players per host.
A conflicting attachment returns `scene_detector_in_use`. Detector handles
belong to their addon worker; another worker cannot use them.

The host delivers `onEvent({name:"scene.request",data:{detectorId},...},ajn)`.
Read, analyze and submit inside that callback. Events run serially per worker.
Cache settings on `start` and `settings.changed`; avoid network/storage work
in this callback. Do not busy-poll or queue expired requests.

```typescript
interface ScenePair {
  requestId: string; epoch: string;
  previousPtsSeconds: number; currentPtsSeconds: number;
  width: number; height: number;
  sourceWidth: number; sourceHeight: number;
  format: "gray8"; stage: "beforeInterpolation"; range: "full";
  remainingMs: number;
  previous: Uint8Array; current: Uint8Array;
}
```

Each array has `width*height` row-major bytes without row padding: full-range,
encoded SDR luma, bilinearly resized from RIFE's input pair. These are not
linear-light or final-display pixels. `sourceWidth/sourceHeight` describe the
processing stage, which may already be resized/upscaled. Later subtitles/OSD
are absent. Analysis runs once per input pair, independent of interpolation
factor. The preceding reduced sample is cached when its identity matches.

`cut` invokes RIFE's existing left-frame substitution. `continuous` interpolates
even when the built-in threshold would classify a cut. `default` delegates this
pair to AJN. AJN retains control of output timestamps and frame cadence.

Keep IDs as opaque strings. Reset cached history on epoch changes, seeks,
geometry changes or timestamp discontinuities. `accepted:false` means a late,
duplicate, revoked or stale submission; it never affects a later pair. An
accepted submission acknowledges delivery, not proof the native thread consumed
it before the deadline. `acceptedPairs` counts native consumption.

## Deadlines, fallback and cost

The native deadline begins **after sample production**. `remainingMs` is the
budget left when the host copied the pair and may be stale by guest delivery.
Missing/invalid decisions use AJN's detector. Three consecutive missed deadlines
suspend the attachment until detach/attach. An independent three-second host
lease expires if the player's lifecycle connection stops renewing it.

Without a detector there is no scene sampling/wait. With one, the implementation
downloads each new hardware frame, reduces it on the CPU, copies a pair to Wasm
and waits for its answer. This is not zero-copy; the deadline does **not** bound
GPU download/resize time. Benchmark the intended GPU, source FPS, resolution
and RIFE order. Reduce sample size/work before increasing deadlines, which can
cause stutter. Small samples may not preserve every cue a full-resolution
MVTools algorithm uses; detector accuracy requires separate evaluation.

Existing worker limits apply: 500 broker calls/second, 128/event, 16 MiB/second
of binary frame transport, a two-second outer event deadline, and Wasm memory
and CPU limits. A pair is at most 115,200 bytes. Multiple players share worker
resources. A slow unrelated callback can cause scene deadlines to expire.

| Status | Meaning/action |
| --- | --- |
| `waitingForPlayer` | No pair yet; check the active profile has RIFE enabled. |
| `pending` | Player is waiting for a decision. |
| `active` | A decision was consumed; this does not assess its quality. |
| `fallback` | A deadline was missed; built-in detection was used. |
| `suspended` | Three consecutive misses; fix workload, detach, reattach. |
| `sampleUnavailable` | Unsupported/sample-failure path, including HDR/interlaced/stereo. |
| `backendUnavailable` | Loaded inference DLLs lack the optional scene ABI. |
| `unavailable` | Unknown native state. |

Player closure releases its detectors; later calls return
`scene_detector_not_found`. Enumerate again. Missing grants give
`permission_denied`; missing support gives `feature_unavailable` or startup
`missing_capability`. Invalid options give `invalid_request`.

## Raw RPC for other Wasm languages

Methods are `sceneDetection.list`, `.attach`, `.read`, `.submit`, `.status`,
`.detach`, with parameters matching the SDK table. Raw `attach` must supply
all three numeric options. A read response is `{pair,byteLength}` followed
immediately after its JSON newline by exactly `byteLength` bytes, previous
plane first. Metadata contains every field above except the arrays. No data is
`{pair:null,byteLength:0}`. Other replies are JSON only. Consume the entire tail
before the next JSON message; use the standard hello/event/error protocol.

## Creator qualification

Test increasing `acceptedPairs` and forced cut/continuous behavior on permitted
SDR footage with working RIFE. Compare detector quality on cuts, pans, flashes
and fades. Test seek, pause/resume, profile/RIFE-order changes, player restart,
disable, competing detectors, deadline suspension/recovery and HDR fallback.
Measure overhead and missed deadlines with the intended number of players.
Protocol tests and compilation alone do not establish smooth playback or
MVTools parity. The complete example source is supplied in `examples/scene-detector`.

### Complete scene-detector manifest.json

```json
{
  "schemaVersion": 1,
  "id": "org.animejanai.scene-detector-example",
  "name": "Scene detector developer example",
  "version": "0.1.0",
  "api": { "major": 1, "minMinor": 8 },
  "permissions": ["player.sceneDetection", "frames.read"],
  "requiredCapabilities": { "sceneDetection": { "major": 1, "minMinor": 0 } },
  "activation": ["manual"],
  "settings": {
    "threshold": { "type": "number", "label": "Mean luma difference threshold", "default": 0.15, "minimum": 0, "maximum": 1 },
    "decision": { "type": "choice", "label": "Developer test mode", "default": "analyze", "choices": ["analyze", "cut", "continuous", "default"] }
  },
  "actions": {
    "attach": { "label": "Enable on available AJN players" },
    "status": { "label": "Show detector status" },
    "detach": { "label": "Restore AJN scene detection" }
  }
}
```

### Complete scene-detector addon.js

```javascript
// A small transport/reference example, not an MVTools port or a quality claim.
// Replace analyze() with a bounded motion-aware detector for production use.
let detectors = [];
let settings;
function analyze(pair, threshold) {
    let difference = 0;
    for (let i = 0; i < pair.previous.length; i++)
        difference += Math.abs(pair.previous[i] - pair.current[i]);
    return difference / (255 * pair.previous.length) > threshold ? "cut" : "continuous";
}
function onEvent(event, ajn) {
    if (event.name === "start" || event.name === "settings.changed") {
        settings = ajn.settings.get();
        return;
    }
    if (event.name === "scene.request") {
        const id = event.data.detectorId;
        const pair = ajn.sceneDetection.read(id);
        if (!pair) return;
        const decision = settings.decision === "analyze" ? analyze(pair, settings.threshold) : settings.decision;
        const result = ajn.sceneDetection.submit(id, pair.requestId, decision);
        const detector = detectors.find(d => d.id === id);
        if (detector) detector.lastPair = { requestId: pair.requestId, epoch: pair.epoch,
            previousPtsSeconds: pair.previousPtsSeconds, currentPtsSeconds: pair.currentPtsSeconds,
            sourceWidth: pair.sourceWidth, sourceHeight: pair.sourceHeight,
            decision, submitted: result.accepted };
        return result;
    }
    if (event.name === "stop" || (event.name === "action" && event.data.id === "detach")) {
        for (const d of detectors) {
            try { ajn.sceneDetection.detach(d.id); } catch (e) { if (e.code !== "scene_detector_not_found") throw e; }
        }
        detectors = []; return { attached: 0 };
    }
    if (event.name === "action" && event.data.id === "attach") {
        const players = ajn.sceneDetection.list();
        detectors = detectors.filter(d => players.some(p => p.playerId === d.playerId));
        for (const p of players) {
            if (p.inUse) continue;
            const d = ajn.sceneDetection.attach(p.playerId, { width: 160, height: 90, deadlineMs: 25 });
            detectors.push({ id: d.detectorId, playerId: p.playerId });
        }
        return { attached: detectors.length, message: "RIFE must be enabled in the selected AJN profile." };
    }
    if (event.name === "action" && event.data.id === "status") {
        return detectors.map(d => {
            try { return { detectorId: d.id, ...ajn.sceneDetection.status(d.id), lastPair: d.lastPair || null }; }
            catch (e) { return { detectorId: d.id, state: e.code }; }
        });
    }
}
```
