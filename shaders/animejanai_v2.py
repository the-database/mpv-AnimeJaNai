import vapoursynth as vs
import os
import subprocess
import logging
import sys
from logging.handlers import RotatingFileHandler

sys.path.append(os.path.dirname(os.path.abspath(__file__)))

import rife_cuda
import animejanai_v2_config

# trtexec num_streams
TOTAL_NUM_STREAMS = 4

core = vs.core
core.num_threads = 4  # can influence ram usage

plugin_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           r"..\..\vapoursynth64\plugins\vsmlrt-cuda")
model_path = os.path.join(plugin_path, r"..\models\animejanai")

formatter = logging.Formatter(fmt='%(asctime)s %(levelname)-8s %(message)s',
                              datefmt='%Y-%m-%d %H:%M:%S')
logger = logging.getLogger('animejanai_v2')

config = {}


def init_logger():
    global logger
    logger.setLevel(logging.DEBUG)
    rfh = RotatingFileHandler(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'animejanai_v2.log'),
                              mode='a', maxBytes=1 * 1024 * 1024, backupCount=2, encoding=None, delay=0)
    rfh.setFormatter(formatter)
    rfh.setLevel(logging.DEBUG)
    logger.addHandler(rfh)


# model_type: HD or SD
# binding: 1 through 9
def find_model(model_type, binding):
    section_key = f'slot_{binding}'
    key = f'{model_type.lower()}_model'

    if section_key in config:
        if key in config[section_key]:
            return config[section_key][key]
    return None


def create_engine(onnx_name):
    onnx_path = os.path.join(model_path, f"{onnx_name}.onnx")
    if not os.path.isfile(onnx_path):
        raise FileNotFoundError(onnx_path)

    engine_path = os.path.join(model_path, f"{onnx_name}.engine")

    subprocess.run([os.path.join(plugin_path, "trtexec"), "--fp16", f"--onnx={onnx_path}",
                    "--minShapes=input:1x3x8x8", "--optShapes=input:1x3x1080x1920", "--maxShapes=input:1x3x1080x1920",
                    "--skipInference", "--infStreams=4", "--builderOptimizationLevel=4",
                    f"--saveEngine={engine_path}", "--tacticSources=+CUDNN,-CUBLAS,-CUBLAS_LT"],
                   cwd=plugin_path)


def scale_to_1080(clip, w=1920, h=1080):
    if clip.width / clip.height > 16 / 9:
        prescalewidth = w
        prescaleheight = w * clip.height / clip.width
    else:
        prescalewidth = h * clip.width / clip.height
        prescaleheight = h
    return vs.core.resize.Spline36(clip, width=prescalewidth, height=prescaleheight)


def upscale2x(clip, engine_name, num_streams):
    if engine_name is None:
        return clip
    engine_path = os.path.join(model_path, f"{engine_name}.engine")

    message = f"upscale2x: scaling 2x from {clip.width}x{clip.height} with engine={engine_name}; num_streams={num_streams}"
    logger.debug(message)
    print(message)

    if not os.path.isfile(engine_path):
        create_engine(engine_name)

    return core.trt.Model(
        clip,
        engine_path=engine_path,
        num_streams=num_streams,
    )


def run_animejanai(clip, container_fps, chain_conf):
    models = chain_conf.get('models', [])
    colorspace = "709"
    colorlv = 1
    try:
        colorlv = clip.get_frame(0).props._ColorRange
    except AttributeError:
        pass
    fmt_in = clip.format.id

    if len(models) > 0:
        if clip.height < 720:
            colorspace = "170m"

        for model_conf in models:

            resize_factor_before_upscale = model_conf['resize_factor_before_upscale']
            if model_conf['resize_height_before_upscale'] != 0:
                resize_factor_before_upscale = 1

            num_streams = max(1, TOTAL_NUM_STREAMS // len(models))

            try:
                clip = vs.core.resize.Spline36(clip, format=vs.RGBH, matrix_in_s=colorspace,
                                              width=clip.width / resize_factor_before_upscale,
                                              height=clip.height / resize_factor_before_upscale)

                clip = run_animejanai_upscale(clip, model_conf, num_streams)
            except:
                clip = vs.core.resize.Spline36(clip, format=vs.RGBS, matrix_in_s=colorspace,
                                              width=clip.width / resize_factor_before_upscale,
                                              height=clip.height / resize_factor_before_upscale)

                clip = run_animejanai_upscale(clip, model_conf, num_streams)

    fmt_out = fmt_in
    if fmt_in not in [vs.YUV410P8, vs.YUV411P8, vs.YUV420P8, vs.YUV422P8, vs.YUV444P8, vs.YUV420P10, vs.YUV422P10,
                      vs.YUV444P10]:
        fmt_out = vs.YUV420P10

    clip = vs.core.resize.Spline36(clip, format=fmt_out, matrix_s=colorspace, range=1 if colorlv == 0 else None)

    if chain_conf['rife']:
        clip = rife_cuda.rife(clip, clip.width, clip.height, container_fps)

    clip.set_output()


def run_animejanai_upscale(clip, model_conf, num_streams):
    if model_conf['resize_height_before_upscale'] != 0 and model_conf['resize_height_before_upscale'] != clip.height:
        clip = scale_to_1080(clip, model_conf['resize_height_before_upscale'] * 16 / 9,
                             model_conf['resize_height_before_upscale'])
    elif clip.height > 1080:
        clip = scale_to_1080(clip)

    # upscale 2x
    return upscale2x(clip, model_conf['name'], num_streams)


# keybinding: 1-9
def run_animejanai_with_keybinding(clip, container_fps, keybinding):
    section_key = f'slot_{keybinding}'

    for chain_key, chain_conf in config[section_key].items():
        # Run the first chain which the video fits the criteria for, if any
        if chain_conf['min_px'] <= clip.width * clip.height <= chain_conf['max_px'] and \
                chain_conf['min_fps'] <= container_fps <= chain_conf['max_fps']:
            logger.debug(f'run_animejanai slot {keybinding} {chain_key}')

            run_animejanai(clip, container_fps, chain_conf)
            return

    clip.set_output()


def init():
    global config
    config = animejanai_v2_config.read_config()
    if config['global']['logging']:
        init_logger()


init()
