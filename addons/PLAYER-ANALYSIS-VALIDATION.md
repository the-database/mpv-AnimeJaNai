# Player analysis qualification — 2026-10-29

This is API 1.12 on the existing 3.8.0 addon integration branch, not a claim that the public 3.9.0 release contains these APIs. The matched preview's `build-info/addon-preview.json` records its exact source commits and evidence. The lighting addon and physical light controllers are separate work.

## Preview 2: Windows long-path correction

The theater-lighting consumer's verification exposed a cache-lock failure in mpv.net at a long installation path. The optional analysis module now normalizes the model path into Windows extended-path form before deriving the cache, lock and temporary paths. Model/engine existence checks, reads, writes and cache publication all use that form. Windows file failures include their numeric error code. The change runs during optional setup/file I/O; per-frame analysis and normal playback code are unchanged. API and addon package versions do not need to change.

The focused regression passed in two fresh **mpv.net** processes with a **286-character model path**, **354-character engine path** and **359-character lock path**. The first built and saved an engine and returned a real color/mask pair; the second returned a pair without rebuilding or changing the cache file. Both unsubscribed and exited normally. The test used existing matched runtime components and a verified ONNX, not a new dependency download. The creator guide now explicitly explains the TensorRT runtime/GPU-kernel prerequisite and Manager's Components setup.

The baseline evidence below was collected for preview 1 and is retained. The 195 host checks and broader playback/performance tests were not repeated for this setup-only path correction.

## Baseline implementation and tests

- The real `character-mask` JavaScript example was compiled to Wasm, installed with reviewed permissions, and run through the public broker in Windows mpv and mpv.net. It received source color and nontrivial IS-Net masks from 1920×1080 Frieren footage using NVDEC/Vulkan, with upscaling and RIFE off. Exported color/mask pairs were inspected visually.
- The fixed SHA256 model and its exact float input/output shapes were verified. TensorRT built an engine locally, then loaded it during playback. A discovered Windows builder-resource search-path problem was fixed and a fresh engine build passed from the workspace directory.
- Seeking changed the epoch and produced a pair at the new PTS. Two separate Wasm addons shared one native producer at different sizes; stopping one preserved the other. Last unsubscribe removed the filter and unloaded `aji_analysis.dll` while playback continued.
- Source observations survived a file change and remained before the processing filter with upscaling and RIFE enabled. Source geometry remained 480×360 while the rendered output was 960×720; the player log confirmed active 2× RIFE.
- A separate compiled Wasm fixture received source color-only samples with just `player.observe` and `frames.read`, without `player.analysis` or mask data. Unsupported software decoding returned an explicit error and did not load the GPU module.
- Existing DirectML/D3D11 player-observation checks passed, including two players, independent sizes, seeks, file changes, other-addon stop, host termination and lease cleanup. The native RIFE scene-hook check passed after its missing legacy RIFE model fixture was supplied.
- 195 host checks passed, including the previously distributed API 1.0 Wasm binary, ownership/grants, paired-data bounds, initial waiting status, and unchanged scene/network/streaming examples. Manager and both native components compiled. Manager retained existing Windows-platform analyzer warnings.

## Measured cost and inactive behavior

One RTX 5090, SDR 1080p anime, 160×90 output, requested maximum 12 analyses/s, five-second observation windows:

| Measurement | Inactive | Active character analysis | After unsubscribe |
| --- | ---: | ---: | ---: |
| Additional playback drops during window | 0 | 0 | 0 |
| Player CPU, percentage of one logical core | 4.0% | 11.4% | See raw evidence |
| Player working set | 587 MiB | 703 MiB | 633 MiB |
| Optional analysis DLL loaded | No | Yes | No |

The 43 pairs read during the active window averaged 8.58 ms of optional GPU work/readback, with a maximum recorded sample age of 9.99 ms. Read frequency and the maximum analysis rate are different: this diagnostic reader polled at 10 Hz and may skip completed pairs. Timings are processing timings, not display or light-output latency. Driver/runtime caches mean process memory need not return exactly to its cold value after cleanup. This short single-machine check is not a performance guarantee for other GPUs or heavily loaded upscaling chains.

No addon subscription means no sample filter, analysis worker, model load/download, CUDA sample buffers or per-frame analysis work. Normal `aji.dll` and `aji_trt.dll` do not import the optional analysis module. Estimation, temporal smoothing, calibration, routing and light-control policy belong in the addon. When active, only reduced color/mask results are read back; decoded full frames and model inference stay on the GPU.

## Limits of this qualification

Character masks currently support NVIDIA CUDA progressive mono SDR inputs. HDR analysis, AMD/DirectML segmentation, Linux, physical lights, projector matching, long-duration performance and all source pixel formats remain unqualified. The live tests used the already-downloaded, verified ONNX; automatic first-use HTTP provisioning is implemented but was not separately exercised by these playback tests. Native setup cancellation and CUDA host-loss cleanup are implemented; only ordinary unsubscribe was directly measured on the new CUDA path. Existing host-loss checks cover the shared lifecycle and D3D11 path.

See [PLAYER-ANALYSIS.md](PLAYER-ANALYSIS.md) for the public contract and [DEVELOPER-GUIDE.md](DEVELOPER-GUIDE.md), Appendix F, for a self-contained example. No physical light system was contacted during qualification.
