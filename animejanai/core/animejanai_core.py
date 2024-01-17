import vapoursynth as vs
import os
import subprocess
import logging
import sys
from logging.handlers import RotatingFileHandler
import rife_cuda 
import animejanai_config

# trtexec num_streams
TOTAL_NUM_STREAMS = 4

core = vs.core
core.num_threads = 4  # can influence ram usage

plugin_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           r"..\..\vapoursynth64\plugins\vsmlrt-cuda")
model_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           r"..\onnx")

formatter = logging.Formatter(fmt='%(asctime)s %(levelname)-8s %(message)s',
                              datefmt='%Y-%m-%d %H:%M:%S')
logger = logging.getLogger('animejanai')
current_logger_info = []
current_logger_steps = []

config = {}


def init_logger():
    logger.setLevel(logging.DEBUG)
    rfh = RotatingFileHandler(os.path.join(os.path.dirname(os.path.abspath(__file__)), '../animejanai.log'),
                              mode='a', maxBytes=1 * 1024 * 1024, backupCount=2, encoding=None, delay=0)
    rfh.setFormatter(formatter)
    rfh.setLevel(logging.DEBUG)
    logger.addHandler(rfh)


def write_current_log_empty():
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), './currentanimejanai.log'), 'w') as f:
        f.write('')


def write_current_log():
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), './currentanimejanai.log'), 'w') as f:
        f.write('\n'.join(current_logger_info) + '\n\n' + '\n'.join([f"{i + 1}. {step}" for i, step in enumerate(current_logger_steps)]))



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

    commands = [os.path.join(plugin_path, "trtexec"), "--fp16", f"--onnx={onnx_path}",
                    "--minShapes=input:1x3x8x8", "--optShapes=input:1x3x1080x1920", "--maxShapes=input:1x3x1080x1920",
                    "--skipInference", "--infStreams=4", "--builderOptimizationLevel=4", 
                    # "--layerPrecisions=*:fp16",
                    # "--layerOutputTypes=*:fp16", "--precisionConstraints=obey", "--inputIOFormats=fp16:chw",
                    # "--outputIOFormats=fp16:chw", "--useCudaGraph", "--noDataTransfers", "--verbose",
                    f"--saveEngine={engine_path}", "--tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT"]

    logger.debug(' '.join(commands))

    subprocess.run(commands,
                   cwd=plugin_path)


def scale_to_1080(clip, w=1920, h=1080):
    if clip.width / clip.height > 16 / 9:
        prescalewidth = w
        prescaleheight = w * clip.height / clip.width
    else:
        prescalewidth = h * clip.width / clip.height
        prescaleheight = h
    return vs.core.resize.Spline36(clip, width=prescalewidth, height=prescaleheight)


def upscale2x(clip, backend, engine_name, num_streams):
    if engine_name is None:
        return clip
    engine_path = os.path.join(model_path, f"{engine_name}.engine")
    network_path = os.path.join(model_path, f"{engine_name}.onnx")

    message = f"upscale2x: scaling 2x from {clip.width}x{clip.height} with engine={engine_name}; num_streams={num_streams}"
    logger.debug(message)
    print(message)

    if backend.lower() == "directml":
        return core.ort.Model(
            clip,
            fp16=True,
            network_path=network_path,
            provider="DML")
    elif backend.lower() == "ncnn":
        return core.ncnn.Model(
            clip,
            fp16=True,
            network_path=network_path)
    else: # TensorRT
        if not os.path.isfile(engine_path):
            create_engine(engine_name)

        return core.trt.Model(
            clip,
            engine_path=engine_path,
            num_streams=num_streams,
        )


def run_animejanai(clip, container_fps, chain_conf, backend):
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
                resize_factor_before_upscale = 100

            num_streams = max(1, TOTAL_NUM_STREAMS // len(models))

            try:
                clip = vs.core.resize.Spline36(clip, format=vs.RGBH, matrix_in_s=colorspace,
                                              width=clip.width * resize_factor_before_upscale / 100,
                                              height=clip.height * resize_factor_before_upscale / 100)

                if resize_factor_before_upscale != 100:
                    current_logger_steps.append(f'Applied Resize Factor Before Upscale: {resize_factor_before_upscale}%;    New Video Resolution: {clip.width}x{clip.height}')

                clip = run_animejanai_upscale(clip, backend, model_conf, num_streams)

                current_logger_steps.append(f"Applied Model: {model_conf['name']};    New Video Resolution: {clip.width}x{clip.height}")
            except:
                clip = vs.core.resize.Spline36(clip, format=vs.RGBS, matrix_in_s=colorspace,
                                              width=clip.width * resize_factor_before_upscale / 100,
                                              height=clip.height * resize_factor_before_upscale / 100)

                if resize_factor_before_upscale != 100:
                    current_logger_steps.append(f'Applied Resize Factor Before Upscale: {resize_factor_before_upscale}%;    New Video Resolution: {clip.width}x{clip.height}')

                clip = run_animejanai_upscale(clip, backend, model_conf, num_streams)
                current_logger_steps.append(f"Applied Model: {model_conf['name']};    New Video Resolution: {clip.width}x{clip.height}")

    fmt_out = fmt_in
    if fmt_in not in [vs.YUV410P8, vs.YUV411P8, vs.YUV420P8, vs.YUV422P8, vs.YUV444P8, vs.YUV420P10, vs.YUV422P10,
                      vs.YUV444P10]:
        fmt_out = vs.YUV420P10

    clip = vs.core.resize.Spline36(clip, format=fmt_out, matrix_s=colorspace, range=1 if colorlv == 0 else None)

    if chain_conf['rife']:
        clip = rife_cuda.rife(clip, clip.width, clip.height, container_fps)
        current_logger_steps.append(f"Applied RIFE Interpolation;    New Video FPS: {container_fps * 2:.3f}")

    clip.set_output()


def run_animejanai_upscale(clip, backend, model_conf, num_streams):

    if model_conf['resize_height_before_upscale'] != 0 and model_conf['resize_height_before_upscale'] != clip.height:
        clip = scale_to_1080(clip, model_conf['resize_height_before_upscale'] * 16 / 9,
                             model_conf['resize_height_before_upscale'])
        current_logger_steps.append(f"Applied Resize Height Before Upscale: {model_conf['resize_height_before_upscale']}px;    New Video Resolution: {clip.width}x{clip.height}")
    elif clip.height > 1080:
        clip = scale_to_1080(clip)
        current_logger_steps.append(f"Applied Resize to Video Larger than 1080p;    New Video Resolution: {clip.width}x{clip.height}")

    # upscale 2x
    return upscale2x(clip, backend, model_conf['name'], num_streams)


# keybinding: 1-9
def run_animejanai_with_keybinding(clip, container_fps, keybinding):

    init()  # testing

    section_key = f'slot_{keybinding}'

    profile_name = config[section_key]['profile_name']

    if int(keybinding) < 10:
        profile_name = f"{keybinding}. {profile_name}"

    current_logger_info.append(f"Upscale Profile: {profile_name}")
    current_logger_info.append(f"Original Video Resolution: {clip.width}x{clip.height};    Original Video FPS: {float(container_fps):.3f}")

    for chain_key, chain_conf in config[section_key].items():
        # Run the first chain which the video fits the criteria for, if any
        #raise ValueError(chain_conf['min_px'] <= clip.width * clip.height <= chain_conf['max_px'])
        if 'chain_' not in chain_key:
            continue
        try:
            print(chain_conf['min_px'])
        except:
            raise ValueError(f"{section_key} {config}")
        if chain_conf['min_px'] <= clip.width * clip.height <= chain_conf['max_px'] and \
                chain_conf['min_fps'] <= container_fps <= chain_conf['max_fps']:
            logger.debug(f'run_animejanai slot {keybinding} {chain_key}')

            current_logger_info.append(f"Active Upscale Chain: {chain_key.replace('chain_', '')};    Resolution Range: {chain_conf['min_resolution']} - {chain_conf['max_resolution']};    FPS Range: {chain_conf['min_fps']} - {chain_conf['max_fps']}")

            run_animejanai(clip, container_fps, chain_conf, config['global']['backend'])
            write_current_log()
            return

    current_logger_info.append(f"No Chains Activated")
    write_current_log()
    clip.set_output()


def init():
    global config, current_logger_info, current_logger_steps
    current_logger_info = []
    current_logger_steps = []
    write_current_log_empty()
    config = animejanai_config.read_config()
    if config['global']['logging']:
        init_logger()


init()
