import os
import pathlib
import re
import subprocess
import sys
import time
import animejanai_config

table = {}


def get_chain_conf(conf, px):
    for chain_key, chain_conf in conf.items():
        if 'chain_' in chain_key:
            if chain_conf['min_px'] <= px <= chain_conf['max_px']:
                return chain_conf
    return None


def get_engine(chain_conf):
    matches = [re.search(r'_([a-zA-Z]*Compact)[_-]', m['name']) for m in chain_conf['models']]
    return '+'.join([f'2x_{m.group(1)}' if m is not None else chain_conf['models'][i]['name'] for i, m in enumerate(matches)])


def printtable(table):
    with open('benchmark.txt', 'w') as f:
        columns = ['480x360', '640x480', '768x576', '1280x720', '1920x1080']
        keys = ['(2x_Compact)',
                '(2x_UltraCompact)',
                '(2x_SuperUltraCompact)',
                '(2x_Compact+2x_Compact)',
                '(2x_Compact+2x_UltraCompact)',
                '(2x_Compact+2x_SuperUltraCompact)',
                '(2x_UltraCompact+2x_SuperUltraCompact)',
                '(2x_UltraCompact+2x_UltraCompact)',
                '(2x_SuperUltraCompact+2x_SuperUltraCompact)']

        f.write('||' + '|'.join(columns) + '|\n')
        f.write('|-|' + '|'.join(['-' for _ in columns]) + '|\n')
        for key in keys:
            value = table[key]
            newrow = []
            for col in columns:
                try:
                    newrow.append(value[col][4])
                except KeyError:
                    newrow.append('')
            f.write('|'+('4x ' if '+' in key else '2x ')+key+'|'+'|'.join(newrow)+'\n')


slots = range(10, 18)
mpv_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), r"..\..")
config = animejanai_config.read_config()

start = time.time()
for filename in os.listdir('./benchmarks'):
    if filename.endswith('.mp4'):
        for slot in slots:
            conf = config[f'slot_{slot}']

            basename = pathlib.Path(filename).stem
            resolution = basename.split('x')
            width = int(resolution[0])
            height = int(resolution[1])
            chain_conf = get_chain_conf(conf, width*height)
            if chain_conf is None:
                print("NO!!!!", slot, conf)
            key = f"({get_engine(chain_conf)})"

            scale = f'{len(chain_conf["models"])*2}x'

            if key not in table:
                table[key] = {}

            if basename in table[key]:
                continue

            print(f'{basename} {scale} {key}: Benchmarking')

            args = [os.path.join(mpv_path, "vspipe.exe"), "--arg", f"video_path=.\\{filename}",
                                "--arg", f"slot={slot}", "--start", "0", "--end", "500",
                                "./animejanai/benchmarks/animejanai_benchmark.vpy",
                                "-p", "."]

            # print(args)

            p = subprocess.run(args, cwd=mpv_path, capture_output=True, text=True)
            # Output x frames in y seconds (z fps)
            outputline = p.stderr.splitlines()[-1]
            try:
                resultfps = re.search(r'\((.+)\)', outputline).group(1)
                print(f'{basename} {scale} {key}: {resultfps}')
                table[key][basename] = [basename, scale, height, key, resultfps]
            except AttributeError:
                print(p.stderr)
                sys.exit(1)

end = time.time()
print(f'Completed in {end - start:.2f} seconds.')

printtable(table)