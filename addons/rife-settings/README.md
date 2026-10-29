# RIFE Settings — development preview

The first feature is the MVTools scene detector used by AnimeJaNai 3.2.1's
`sc_mode=2`: `Super(pel=1)`, `Analyse(isb=True)` and
`SCDetection(thscd1=240, thscd2=130)`. RIFE itself remains in the AJN GPU pipeline.

Install the matching API 1.11 host and player update, including `ajn-mvtools.dll`
beside `mpv.exe`/`mpvnet.exe`. The native capability manifest must include that
DLL and advertise `privateMvtoolsSceneAbi: 1`. An unmodified 3.8.0 addon preview
does not have this capability. Use a separate preview installation for testing.

Build `manifest.json` and `addon.js` with the standard addon build command,
then install the `.ajnaddon` in Manager → Addons. Approve scene control and frame
access, enable RIFE in the playback profile, and enable the addon. It attaches
automatically to open/new AJN players. It does not turn RIFE on or select models.

- `mvtools`: the original motion-analysis core runs on full-resolution planar
  YUV/gray, retaining chroma and integer sample depth. Default thresholds are
  240 and 130. Lower thresholds detect more cuts; higher thresholds detect fewer.
- `enhanced`: retains the MVTools decisions and adds checks for weaker cuts
  after quiet shots. Select this mode in Manager to try the quality improvement;
  keep thresholds at 240/130 initially. It checks recent motion, brightness
  distribution and image structure using the same downloaded frames, with no
  lookahead. It requires `privateEnhancedSceneAbi: 1` in the matching runtime.
  This is optional; `mvtools` remains the default. Additional cut recovery starts
  after three source pairs following a seek or format change. It can still miss
  cuts or mistake unusual animation for a transition.
- `default`: releases the detector and restores AJN's existing scene detection.
- Stopping/disabling the addon releases every owned detector. Another addon
  already controlling a player keeps ownership.

The optional native library is loaded lazily from the trusted AJN installation;
it is not supplied as executable content inside the addon. Without an enabled
detector there is no new frame download, analysis or library loading. Enabled
MVTools analysis uses CPU time and GPU readback. Status reports consumed pairs,
cuts, changed blocks and last-pair analysis time (including readback/conversion).
Unsupported HDR/interlaced/stereo inputs retain built-in detection and show
`sampleUnavailable`; missing/broken native libraries are reported explicitly.

This preview uses the pinned upstream MVTools search core and exact SSE2 block
kernels on x64. Enhanced mode adds AJN's conservative quiet-shot cut checks.
Matching the affected user's old DLL revision, visual results on their scenes,
and real-time cost on their hardware remain qualification work. The addon is
named RIFE Settings so future RIFE controls can live here; no unrelated controls
are implemented in this version.
