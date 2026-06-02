import sys, os, pathlib
import vapoursynth as vs
import animejanai_core

# Instructions for running this benchmarking tool
# 1. Pass the source video and slot number as VSPipe args (--arg video_path=... --arg slot=...).
# 2. The slot must match a slot configured in animejanai.conf (1-9 for user slots,
#    or 1010/1011/1012 for the built-in Compact/UltraCompact/SuperUltraCompact templates).
# 3. Run the following from the main mpv directory:
#     .\VSPipe.exe --arg video_path=.\video.mkv --arg slot=1010 --start 0 --end 2000 .\animejanai\benchmarks\animejanai_benchmark.vpy -p .
# Or use animejanai_benchmark_all.py to sweep the bundled benchmark videos automatically.

required_args = {'slot', 'video_path'}

args = dict(globals())

for arg in required_args:
    if arg not in args:
        print(f"{arg} is a required argument")
        sys.exit(1)

video_path = os.path.abspath(args['video_path'])
clip = vs.core.ffms2.Source(source=video_path)
slot = int(args['slot'])

animejanai_core.run_animejanai_with_keybinding(clip, clip.fps, slot)