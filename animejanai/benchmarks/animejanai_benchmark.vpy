import sys, os, pathlib
import vapoursynth as vs
import animejanai_core

# Instructions for running this benchmarking tool
# 1. Edit the path of the source video below, from ./video.mkv to the path of
#    the video you want to benchmark upscaling on
# 2. Set the slot below, to a value between 1-9 to select the slot, configured in animejanai.conf,
#    that you want to benchmark
# 3. Run the following command from the main mpv directory:
#     .\VSPipe.exe --start 0 --end 2000 ./portable_config/shaders/animejanai_benchmark.vpy -p .

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