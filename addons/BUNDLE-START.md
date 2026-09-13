# Try the addon foundation

This standalone Windows x64 developer preview exercises the addon host. .NET is included; no SDK or GPU setup is required to run the counter example. Native sessions and frame samples additionally require a matching AJN installation and explicit media/profile approvals; see `NATIVE-MEDIA.md` and `FRAMES.md`, or use the integrated preview. The generic sample inspector exercises binary image samples and host timers. Plex and lighting addons are not included.

The service inspector exercises approved HTTP/UDP destinations and optional
saved header credentials. Use Manager to review access before invoking its
request action. It sends nothing on startup; see `NETWORK.md` for its limits.

The output inspector exercises processed video/audio delivery to an approved
HTTP receiver. It requires the matching integrated native output runtime and
NVIDIA encoding hardware. It sends nothing until its explicit action is used.
See `OUTPUTS.md` for source/profile/receiver permissions and supported formats.

Extract the ZIP to a writable folder. Open PowerShell 7 there and run:

```powershell
./tools/run-example.ps1
```

The script installs the bundled counter addon with permissions to log and read/write its own storage. It runs the addon inside Wasmtime and saves its data in `example-data` beside the bundle. Run it again to verify that the counter survives restart. It does not grant network, general file, or process access.

Read and change a setting:

```powershell
./host/ajn-addon.exe settings org.animejanai.counter ./example-data
'{"greeting":"Good evening"}' | Set-Content ./greeting.json -Encoding utf8NoBOM
./host/ajn-addon.exe configure org.animejanai.counter ./example-data ./greeting.json
./host/ajn-addon.exe action org.animejanai.counter ./example-data ./runtime/wasmtime.exe status
./host/ajn-addon.exe replay org.animejanai.counter ./example-data ./runtime/wasmtime.exe ./examples/counter/events.json
```

For your own addon, download the pinned official developer tools first. This is a separate, explicit download and does not execute addon setup scripts:

```powershell
./tools/bootstrap.ps1
$tools = Get-Content ./.tools/tools.json -Raw | ConvertFrom-Json
./host/ajn-addon.exe new ./my-addon org.example.mine
# Edit my-addon/addon.js and manifest.json, then:
./host/ajn-addon.exe build ./my-addon $tools.javy.path ./my-addon.ajnaddon
./host/ajn-addon.exe inspect ./my-addon.ajnaddon
```

See `README.md`, `API.md`, and `ROADMAP.md` for installation permissions, limits, and remaining integration work. Buildable AJN host/SDK source is in `source`. Wasmtime's license is in `licenses`; its official source is at https://github.com/bytecodealliance/wasmtime/tree/v48.0.2. The counter's compiled JavaScript runtime comes from Javy 9.1.0 at https://github.com/bytecodealliance/javy/tree/v9.1.0.
