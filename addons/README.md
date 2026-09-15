# AJN addon foundation — developer preview

## Start here

This branch implements **API 1.7 preview**, including
[HTTP, proxy and credential APIs](HTTP-SERVER.md), reviewed HTTPS listeners and
the [streaming API](STREAMING.md). The native qualification and packaging checks
in [STREAMING-TESTS.md](STREAMING-TESTS.md) must pass before a matching runtime is
released. The sealed integration.1 release remains the API 1.6 baseline and does
not contain these additions.

| Your goal | Guide |
| --- | --- |
| Develop an addon using one self-contained release document | [Complete addon developer guide](DEVELOPER-GUIDE.md) — setup, tutorial, contracts, SDK declarations and complete examples |
| Install, use or troubleshoot an addon | [User guide](USER-GUIDE.md) |
| Make your first addon | [Creator tutorial](CREATOR-GUIDE.md) — build, permissions, saved data, settings, update and rollback |
| Add video, image samples, service access or output | [Recipes and complete examples](CREATOR-RECIPES.md) |
| Look up protocol, limits and compatibility | [API reference](API.md) and [SDK types](sdk/ajn.d.ts) |
| Resolve a specific error | [Troubleshooting](TROUBLESHOOTING.md) |
| Build, package, update or recover a preview | [Release integration](RELEASE-INTEGRATION.md) |

Full previews and developer bundles include offline HTML guides as well as this
source documentation. In Manager, open **Addons → Help**. Opening Addons prepares
local support automatically; there is no manual connection step.

## Implementation status

This working preview combines lifecycle, declarative settings/actions, JSON-RPC and developer replay with an enforced Wasm sandbox. The coordinated `integration/addons` branches build on current upstream, including the merged 3.6.1 fixes. The original feature branches and sealed previews remain historical snapshots.

**Working now:** compile a JavaScript addon, inspect/install an unsigned development package with explicit permissions, run it in an isolated worker, save private addon data, change typed settings, invoke declared actions, replay events, restore the previous package, and disable its registration. A persistent per-user host and the companion Manager Addons tab share one activation controller per addon. Tests cover library behavior, real Windows workers, the private management connection, and rendered Manager controls.

**Native sessions now work:** the optional Windows provider runs independent GPU processing sessions from explicitly approved local media and saved profile snapshots. Manager provides resource consent and revocation. Real Wasm-to-DirectML tests cover concurrent 2× upscaling and independent controls. See [NATIVE-MEDIA.md](NATIVE-MEDIA.md) for setup, supported formats, limits, and the session-controller example.

**Frame samples now work:** an optional native filter reduces SDR DirectML images on the GPU and sends bounded binary samples through the host to Wasm. Host timers provide serialized callbacks, with missed ticks coalesced. Tests exercise actual moving images, two concurrent upscaling sessions, independent pause/seek, permission checks and cleanup. See [FRAMES.md](FRAMES.md) and the generic sample inspector.

**Service access now works:** Manager reviews HTTP/HTTPS/UDP destinations, and the broker enforces their pinned addresses, deadlines and limits. HTTP is asynchronous with bounded binary responses. Optional headers are protected for the Windows user and attached only to their approved service. See [NETWORK.md](NETWORK.md) and the service inspector.

**Encoded output now works:** addons can request owned GPU video/audio outputs to approved HTTP receivers. Encoding and continuous transport stay in trusted code. Tests decode received streams, check three codecs, run concurrent outputs and revoke active access. See [OUTPUTS.md](OUTPUTS.md), the output inspector, and [NATIVE-OUTPUT.md](NATIVE-OUTPUT.md) for hardware and format limits.

**Player and login activation now work:** the trusted player bridge consolidates multiple players into one addon worker and releases ownership when each player exits. Manager offers opt-in Windows sign-in startup, separately from package permissions. See [LIFECYCLE.md](LIFECYCLE.md) for installation, behavior and validation limits.

**Remote media now works:** a separate input permission lets addons process media from approved HTTP/HTTPS services, including range seeking and remote-to-remote encoded delivery. The trusted reader supplies bytes directly to owned native sessions. See [REMOTE-INPUTS.md](REMOTE-INPUTS.md) for the generic inspector, bounds and supported formats.

**Normal-player samples now work:** separately approved observers can read small processed SDR images from ordinary mpv and mpv.net playback. Multiple addons share one producer per player, with independent sample sizes and rates. Tests cover actual Wasm, two players, seeking, independent unsubscribe and host termination while playback continues. See [PLAYER-FRAMES.md](PLAYER-FRAMES.md) and the generic player inspector.

**Remaining integration:** API 1.7 native qualification and release packaging, discovery, unrestricted transport APIs, final-display/HDR/TensorRT samples, website catalog/signatures, automatic updates, and Linux enforcement. Neither the Plex addon nor the lighting addon is implemented here.

API 1.7 is a **preview contract**, not a frozen public compatibility promise. Existing 1.0–1.6 calls remain supported. See [API.md](API.md), [ARCHITECTURE.md](ARCHITECTURE.md), and [ROADMAP.md](ROADMAP.md).

## Try it on Windows x64

Install .NET SDK 10. From this repository, run PowerShell 7:

```powershell
./addons/tools/bootstrap.ps1
dotnet build ./addons/src/AnimeJaNai.Addons -c Release
$ajn = './addons/src/AnimeJaNai.Addons/bin/Release/net10.0/ajn-addon.dll'
$tools = Get-Content ./addons/.tools/tools.json -Raw | ConvertFrom-Json

dotnet $ajn new ./my-first-addon org.example.first
dotnet $ajn build ./my-first-addon $tools.javy.path ./first.ajnaddon
dotnet $ajn inspect ./first.ajnaddon
dotnet $ajn install-dev ./first.ajnaddon ./addon-data 'log.write,storage.read,storage.write'
dotnet $ajn run org.example.first ./addon-data $tools.wasmtime.path
dotnet $ajn run org.example.first ./addon-data $tools.wasmtime.path
```

The second run returns `starts: 2`. Engine files, mpv, a GPU, and an AJN installation are not needed for this example. `new` creates the source, manifest, and editor types. Edit `onEvent` in `addon.js`, change the version, and build to a **new** package filename.

There are no permissions by default. `install-dev` deliberately installs an unsigned local package; list the permissions you approve after inspecting it. Grants belong to that exact package hash. Installing an update does not silently inherit grants. A hash verifies integrity, not publisher identity.

Configure the example or run its declared action:

```powershell
'{"greeting":"Good evening"}' | Set-Content ./greeting.json -Encoding utf8NoBOM
dotnet $ajn configure org.example.first ./addon-data ./greeting.json
dotnet $ajn settings org.example.first ./addon-data
dotnet $ajn action org.example.first ./addon-data $tools.wasmtime.path status
dotnet $ajn replay org.example.first ./addon-data $tools.wasmtime.path ./addons/examples/counter/events.json
```

An action starts an idle addon temporarily, sends `start`, then `action`, then `stop`. An action on a running library-managed addon shares its worker. Settings are host-owned and read-only to addons. A standalone CLI configuration command affects the next call to `settings.get`; the embedding controller also delivers `settings.changed` to its active worker.

After installing two versions:

```powershell
dotnet $ajn rollback org.example.first ./addon-data
dotnet $ajn disable org.example.first ./addon-data
```

Rollback switches code and its previously approved grant. Settings and private storage are retained. Incompatible saved settings stop activation; Manager shows which fields need repair and displays valid defaults without changing the saved values until the user saves. No settings migration scripts run automatically. `disable` removes the registration for future starts.

## Manager and persistent host

The companion `AnimeJaNaiManager` branch `feature/addon-manager` provides the Addons tab. It expects `addon-host/ajn-addon.exe` and `addon-host/runtime/wasmtime.exe` under the AJN root. Addon data lives in `addons` under the Manager data directory. Use the integrated preview package, or follow [MANAGEMENT.md](MANAGEMENT.md) to assemble a development installation.

Manager reviews a local package before installation, starts permission checkboxes unchecked, and binds the approval to the exact reviewed archive hash. It renders typed settings, declared actions, worker status and bounded logs. Removing an addon stops it and retains its settings/storage. A manually started addon continues after Manager closes; `on_manager` activation ends when the last relevant Manager connection closes.

**Performance limits** lets you save a processing-session limit from 1 to 16 across
all addons (default two). An addon chooses its requested session count within
that limit. Lowering the host limit does not interrupt existing work; it limits
new admissions until sessions close. An explicit `serve` capacity argument is
shown as a read-only override of the saved choice.

The host can also be started with `serve <data-directory> <wasmtime.exe>`. An exclusive data-directory lease prevents standalone CLI writes or workers from competing with an active service. Use Manager to manage that running service, or use a separate directory for CLI development. The service exits after about 30 idle seconds with no clients or running workers. It does not register itself for Windows startup.

## Develop and test

```powershell
./addons/tools/test.ps1
# Contract/library tests without downloading the Windows runtimes:
./addons/tools/test.ps1 -UnitOnly
```

The default suite builds and executes real Javy/Wasmtime fixtures, including hangs and malformed messages. Results are saved in `.work/validation/run-*/results.json`. `.tools`, `.work`, compiled packages, and build outputs are ignored by Git. GitHub Actions runs the full suite on Windows Server 2022.

The SDK is a convenience layer, not an access boundary. Authors can replace it or use another language that produces a compatible core Wasm module and implements the documented protocol. This first JavaScript SDK uses synchronous event callbacks; Node.js packages, browser APIs, arbitrary native executables, and asynchronous JavaScript promises are not supported.

## Runtime maintenance

The bootstrap script downloads official **Wasmtime 48.0.2** and **Javy 9.1.0**, checking pinned archive hashes. The host verifies the runtime executable hash at launch; the builder verifies the compiler. Upgrade those pins together with the real-worker test results. Runtime vulnerabilities remain part of the maintenance responsibility; isolation is not a guarantee that defects can never exist.

The runtime and its dependencies are third-party components with their own licenses. The source repository's existing license applies to AJN code. [Wasmtime](https://github.com/bytecodealliance/wasmtime/tree/v48.0.2), [Javy](https://github.com/bytecodealliance/javy/tree/v9.1.0).
