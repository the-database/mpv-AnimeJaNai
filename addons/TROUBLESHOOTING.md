# Addon troubleshooting

Start with the message in Manager's Addons tab. Expand **Technical details** to
copy the error code and message. An error during installation is different from
an error inside a running addon's action. A completed callback also does not mean
an asynchronous media/network operation succeeded; check its terminal status.

## Installation and Manager

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

## Sources, networking and playback

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

## Creator build/runtime mistakes

Use PowerShell 7 and the tools from the same preview. `compiler_mismatch` or
`runtime_mismatch` means the executable does not match the pinned tool expected
by the host. Run the provided bootstrap and use its paths; do not disable hash
checks to make a different binary run.

`already_exists` during `new` or `build` means the command is protecting an
existing source folder/package. Choose a new folder or output filename.
Keep CLI test data separate from a running Manager's data directory.

If JavaScript mentions missing `fetch`, `require`, `setInterval` or DOM objects,
it was written for another runtime. Use the SDK calls described in the
[creator guide](CREATOR-GUIDE.md). A promise returned from `onEvent` is rejected;
use synchronous callbacks and host timers. For unexpected process termination,
check the callback deadline, message/binary sizes, CPU/memory quotas and logs in
[API limits](API.md#limits-and-failure-semantics).

When reporting a creator issue, include a minimal source example, manifest,
build command, AJN/API/runtime versions, reproducible steps and the exact error.
For native media include the backend, GPU/driver and non-private source format.
Avoid service secrets, full authenticated URLs and personal media paths in public
reports. [Recipes](CREATOR-RECIPES.md) links the complete maintained examples.
