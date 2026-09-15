# Create an AJN addon

Build a small addon, run it, save its settings, and package an update. This guide
uses **plain JavaScript** and AJN API 1.7's supported API 1.0 subset. The tutorial
needs no GPU or inference engines. Video features can be added afterwards using
the same package and permission model.

AJN compiles JavaScript to portable WebAssembly. It is not Node.js or a browser:
there is no `require`, `fetch`, DOM, filesystem access, process launching or
`setInterval`. Call the AJN SDK for supported operations. Callbacks finish
synchronously; use AJN timers and session/request handles for continuing work.

## 1. Prepare a workspace

Use Windows x64 and **PowerShell 7**. Choose one starting point:

- **Standalone developer bundle:** extract the matching
  `AnimeJaNai-Addon-Foundation-…-win-x64.zip`. It contains the ready-to-run CLI,
  runtime, SDK, source, examples and guides. Open PowerShell in that folder.
- **Full AJN addon preview:** open PowerShell in the folder containing
  `AnimeJaNaiManager.exe`. Use the alternate path setup below.
- **Source repository:** build the CLI with .NET SDK 10. The repository
  [README](README.md#try-it-on-windows-x64) gives the commands. Follow the same
  tutorial with `dotnet ajn-addon.dll` instead of the self-contained executable.

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

## 2. Define the addon

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

## 3. Write the behavior

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

## 4. Build and test the package

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

## 5. Package an update

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

## Choose the right extension API

| You want to… | API and required capability | Permissions / user choices |
| --- | --- | --- |
| Show settings and actions | `settings.get`, event callback; settings 1.0 | Manifest definitions; no added permission |
| Save addon state | `storage.get/set` | `storage.read/write` |
| Do periodic work | `timers.set/clear`; timers 1.0 | No added permission; use serialized timer events |
| Process independent videos | `sessions`; sessions 1.1 | `sessions.manage`, approved files/profiles |
| Read small samples from an owned session | `frames`; frames 1.0 | `frames.read` + `sessions.manage` |
| Observe normal AJN playback | `playerFrames`; playerFrames 1.0, API 1.6 | `player.observe` + `frames.read`; no playback controls |
| Customize RIFE scene decisions | `sceneDetection`; 1.0, API 1.8 | `player.sceneDetection` + `frames.read`; see [SCENE-DETECTION.md](SCENE-DETECTION.md) |
| Talk to a service/device | `network`; network 1.0 | `network.connect`, approved destination; optional `credentials.use` |
| Process a remote video | `sessions.openRemote`; remoteSources 1.0, API 1.5 | `media.input` + `sessions.manage` + `network.connect`, approved service/profile |
| Deliver encoded video/audio | `outputs`; outputs 1.0, API 1.4 | `media.output` + session/network grants, approved input/profile/receiver; remote input adds its own requirements |
| Receive client requests | `httpServer`; httpServer 1.0, API 1.7 | `network.listen`, reviewed listener and HTTPS certificate where applicable |
| Forward HTTP/WebSockets | `httpProxy`; httpProxy 1.0, API 1.7 | `network.proxy` + `network.connect`, approved upstream; forwarding also uses a listener |
| Probe and serve processed media | `mediaProbe`, `mediaStreams`, `outputPlayback`; API 1.7 | Source/profile, input/output/session grants, plus listener and remote-source grants as needed; see `STREAMING.md` |
| Use a client's upstream credential | `requestCredentials`; API 1.7 | `credentials.delegate` + `credentials.use`, declared sensitive fields and approved destination; addon must validate the client |
| Burn or extract subtitles | `outputPlayback`, `subtitles`; API 1.7 | Selected source/track, explicit external resource approvals and the matching native capability |

Read [Recipes and examples](CREATOR-RECIPES.md), [API reference](API.md) and
[SDK types](sdk/ajn.d.ts). Each feature guide documents its exact limits. Do not
infer a native feature from the application version alone: `ajn.info()` reports
optional capabilities and granted permissions. Required capabilities belong in
the manifest; optional ones need a runtime check and a useful fallback.

## Lifecycle and time budgets

The same running worker handles `start`, `action`, `settings.changed`, `timer`
and `stop` events one at a time. Several players/Manager windows can hold startup
reasons for the same addon without creating duplicate workers. `start` is not a
notification for each individual player. Use `playerFrames.list()` for attached
players. [Lifecycle](LIFECYCLE.md) specifies startup and stop behavior.

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

## Make an addon easy to use

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

## Test before sharing

Check first install, no permissions, partial permissions, empty resource lists,
normal operation, a closed player/service, repeated start/stop, changed settings,
an update, rollback and restart. Confirm one addon's failure does not stop other
work. Hardware-dependent addons need actual playback tests on supported hardware;
a passing JavaScript build does not validate a media pipeline.

The maintained tutorial check extracts this guide's manifest and JavaScript,
compiles and runs them, checks denied permissions, saved state, settings, update
and rollback. It is [tools/test-tutorial.ps1](tools/test-tutorial.ps1). The host's
separate suite also runs an unchanged older API 1.0 binary. Keep those old-client
fixtures when adding a feature; do not silently change existing field meanings.

For an LLM-assisted implementation, provide this guide, the API reference, the
relevant feature guide and `ajn.d.ts`. Ask it to use only those documented APIs,
keep callbacks synchronous and bounded, explain every requested permission,
handle unavailable capabilities, and supply reproducible build/test steps. Check
its output for invented Node/browser APIs and undocumented native shortcuts.

See [Troubleshooting](TROUBLESHOOTING.md) for error codes and recovery. API 1.7 is
still a preview contract; public stability and deprecation policy must be frozen
before a community release.
