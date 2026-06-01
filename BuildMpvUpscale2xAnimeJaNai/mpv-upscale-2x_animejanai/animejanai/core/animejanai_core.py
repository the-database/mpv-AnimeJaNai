import vapoursynth as vs
import os
import re
import subprocess
import logging
import sys
from logging.handlers import RotatingFileHandler
import rife_cuda
import animejanai_config
import zlib

# trtexec num_streams
TOTAL_NUM_STREAMS = 4

# Default (static) TensorRT engine settings, used when a config doesn't specify
# trt_engine_settings (e.g. older configs created before that option existed). Keep this in
# sync with the [global] trt_engine_settings in animejanai.conf. Dynamic engines are opt-in:
# a config must explicitly set trt_engine_settings (with min/opt/max shapes) to get one.
DEFAULT_TRT_ENGINE_SETTINGS = (
    "--stronglyTyped --optShapes=input:%video_resolution% "
    "--inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --builderOptimizationLevel=5 "
    "--tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT --skipInference"
)

core = vs.core
core.num_threads = 4  # can influence ram usage

plugin_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           r"..\..\vs-plugins\vsmlrt-cuda")
model_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           r"..\onnx")

formatter = logging.Formatter(fmt='%(asctime)s %(levelname)-8s %(message)s',
                              datefmt='%Y-%m-%d %H:%M:%S')
logger = logging.getLogger('animejanai')
current_logger_info = []
current_logger_steps = []

config = {}

# Memoized per-session TensorRT/GPU identity used in the engine cache key (see get_engine_path).
# Querying the GPU is mildly expensive, and both create_custom_engine and upscale2x_trt must agree
# within a run, so compute once and cache.
_trt_version_token = None
_gpu_tokens = {}
# Stale-engine cleanup runs once per session (see clean_stale_engines / upscale2x_trt).
_engines_cleaned = False


def init_logger():
    logger.setLevel(logging.DEBUG)
    rfh = RotatingFileHandler(os.path.join(os.path.dirname(os.path.abspath(__file__)), '../animejanai.log'),
                              mode='a', maxBytes=1 * 1024 * 1024, backupCount=2, encoding=None, delay=0)
    rfh.setFormatter(formatter)
    rfh.setLevel(logging.DEBUG)
    logger.handlers.clear()
    logger.addHandler(rfh)
    logger.addHandler(logging.StreamHandler())


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


def _get_trt_version_token():
    """Current TensorRT runtime version as 'X.Y.Z', memoized. Folded into the engine cache key so a
    TensorRT upgrade produces a new engine filename (and a clean rebuild) instead of loading an
    incompatible cached engine. Parsed the same way as vsmlrt.py's parse_trt_version."""
    global _trt_version_token
    if _trt_version_token is None:
        try:
            v = int(core.trt.Version()["tensorrt_version"])
            if v < 10000:
                major, minor, patch = v // 1000, (v // 100) % 10, v % 100
            else:
                major, minor, patch = v // 10000, (v // 100) % 100, v % 100
            _trt_version_token = f"{major}.{minor}.{patch}"
        except Exception as e:
            logger.debug(f"Could not query TensorRT version ({e!r}); using 'unknown'")
            _trt_version_token = "unknown"
    return _trt_version_token


def _get_gpu_token(device_id=0):
    """Sanitized name + compute-capability of the build GPU, memoized per device. Folded into the
    engine cache key so swapping GPUs produces a new engine filename instead of loading an engine
    built for a different GPU architecture."""
    if device_id not in _gpu_tokens:
        try:
            props = core.trt.DeviceProperties(device_id)
            name = props["name"]
            name = name.decode() if isinstance(name, (bytes, bytearray)) else str(name)
            token = re.sub(r"[^A-Za-z0-9._-]", "", name.replace(" ", "-")) or f"device{device_id}"
            try:
                token = f"{token}-sm{props['major']}"
            except Exception:
                pass
            _gpu_tokens[device_id] = token
        except Exception as e:
            logger.debug(f"Could not query GPU properties for device {device_id} ({e!r}); using 'unknown'")
            _gpu_tokens[device_id] = "unknown"
    return _gpu_tokens[device_id]


def _device_id_from_settings(trt_settings):
    """trtexec builds on device 0 unless --device=N is given; match the cache key to that device."""
    m = re.search(r"--device[=\s]+(\d+)", trt_settings or "")
    return int(m.group(1)) if m else 0


def _current_engine_suffix(device_id=0):
    """Readable, parseable tail identifying the current GPU + TensorRT version. Also used by
    clean_stale_engines to tell live engines from stale ones."""
    return f".trt-{_get_trt_version_token()}.gpu-{_get_gpu_token(device_id)}.engine"


def get_engine_path(onnx_name, trt_settings):
    # {onnx_name}.{crc32(all trt build flags)}.trt-{version}.gpu-{device}.engine
    # The CRC already covers every trtexec flag (precision, engine type, opt level, shapes, ...);
    # the trt/gpu tokens cover the two things flags don't: the TensorRT version and the GPU.
    device_id = _device_id_from_settings(trt_settings)
    return os.path.join(
        model_path,
        f"{onnx_name}.{zlib.crc32(trt_settings.encode())}{_current_engine_suffix(device_id)}"
    )


def clean_stale_engines(device_id=0):
    """Delete cached TensorRT engines that can never be a cache hit again on this setup: any engine
    not built for the current TensorRT version AND current GPU. Current-environment engines are kept
    no matter how rarely they're used - staleness is judged solely by the trt/gpu tokens in the
    filename, never by age. Legacy-named engines (pre-token scheme) lack the suffix and are removed
    too, since the new cache key orphans them.

    Note: with an explicit multi-GPU --device=N setup this keys off the current device, so engines
    deliberately built for a different device may be removed - acceptable given GPU-mismatch cleanup
    was explicitly opted into."""
    suffix = _current_engine_suffix(device_id)
    try:
        entries = os.listdir(model_path)
    except OSError as e:
        logger.debug(f"Could not list engine directory for cleanup ({e!r})")
        return
    for name in entries:
        if not name.endswith(".engine") or name.endswith(suffix):
            continue
        try:
            os.remove(os.path.join(model_path, name))
            msg = f"Removed stale TensorRT engine (different GPU or TensorRT version): {name}"
            logger.debug(msg)
            current_logger_steps.append(msg)
        except OSError as e:
            logger.debug(f"Could not remove stale engine {name} ({e!r})")


def create_custom_engine(onnx_name, trt_settings):
    onnx_path = os.path.join(model_path, f"{onnx_name}.onnx")
    if not os.path.isfile(onnx_path):
        raise FileNotFoundError(onnx_path)

    engine_path = get_engine_path(onnx_name, trt_settings)

    commands = [os.path.join(plugin_path, "trtexec"), f"--onnx={onnx_path}", f"--saveEngine={engine_path}",
                    *trt_settings.split(" ")]

    logger.debug(' '.join(commands))

    result = subprocess.run(commands, cwd=plugin_path)
    if result.returncode != 0:
        # Surface trtexec failures so upscale2x_trt's existence check below isn't fooled by a half-written engine file.
        raise RuntimeError(
            f"trtexec failed (exit {result.returncode}) building engine for {onnx_name}; "
            f"check animejanai.log and your trt_engine_settings."
        )


def scale_to_1080(clip, w=1920, h=1080):
    if clip.width / clip.height > 16 / 9:
        prescalewidth = w
        prescaleheight = w * clip.height / clip.width
    else:
        prescalewidth = h * clip.width / clip.height
        prescaleheight = h
    return vs.core.resize.Spline36(clip, width=prescalewidth, height=prescaleheight)


def upscale2x(clip, backend, engine_name, num_streams, trt_settings=None):
    if engine_name is None:
        return clip
    network_path = os.path.join(model_path, f"{engine_name}.onnx")

    message = f"upscale2x: scaling 2x from {clip.width}x{clip.height} with engine={engine_name}; num_streams={num_streams}"
    logger.debug(message)
    # print(message)

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

    # TensorRT — default to static engine settings when the config doesn't specify any
    # (this makes older configs static too). Dynamic engines are opt-in: a config must
    # explicitly set trt_engine_settings. Then substitute the actual clip dimensions.
    if trt_settings is None:
        trt_settings = DEFAULT_TRT_ENGINE_SETTINGS
    trt_settings = trt_settings.replace("%video_resolution%", f"1x3x{clip.height}x{clip.width}")

    return upscale2x_trt(clip, engine_name, num_streams, trt_settings)


def upscale2x_trt(clip, engine_name, num_streams, trt_settings):
    global _engines_cleaned
    if not _engines_cleaned:
        # Once per session, and only on the TensorRT path (DirectML/ncnn never reach here), now that
        # the current GPU + TRT version are known. Reusing an engine from a different GPU/TRT version
        # would error, so drop the now-unusable ones instead of leaving them to accumulate.
        clean_stale_engines(_device_id_from_settings(trt_settings))
        _engines_cleaned = True

    engine_path = get_engine_path(engine_name, trt_settings)
    if not os.path.isfile(engine_path):
        create_custom_engine(engine_name, trt_settings)

    if not os.path.exists(engine_path):
        logger.debug("Engine failed to generate, exiting. Please make sure your TensorRT Engine Settings are appropriate for the type of model you are using.")
        sys.exit(1)

    return core.trt.Model(
        clip,
        engine_path=engine_path,
        num_streams=num_streams
    )


def _resize_and_upscale(clip, fmt, colorspace, resize_factor_before_upscale,
                        backend, model_conf, trt_settings, num_streams):
    clip = vs.core.resize.Spline36(clip, format=fmt, matrix_in_s=colorspace,
                                   width=clip.width * resize_factor_before_upscale / 100,
                                   height=clip.height * resize_factor_before_upscale / 100)
    if resize_factor_before_upscale != 100:
        current_logger_steps.append(
            f'Applied Resize Factor Before Upscale: {resize_factor_before_upscale}%;    '
            f'New Video Resolution: {clip.width}x{clip.height}'
        )
    return run_animejanai_upscale(clip, backend, model_conf, trt_settings, num_streams)


def run_animejanai(clip, container_fps, chain_conf, backend):
    logger.debug(f"chain_conf {chain_conf}")
    models = chain_conf.get('models', [])
    trt_settings = config['global'].get("trt_engine_settings")
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

            # Try fp16 (RGBH) first; fall back to fp32 (RGBS) if the GPU/driver rejects half-precision resize.
            try:
                clip = _resize_and_upscale(clip, vs.RGBH, colorspace, resize_factor_before_upscale,
                                           backend, model_conf, trt_settings, num_streams)
            except Exception as e:
                logger.debug(f"RGBH path failed ({e!r}); retrying with RGBS")
                clip = _resize_and_upscale(clip, vs.RGBS, colorspace, resize_factor_before_upscale,
                                           backend, model_conf, trt_settings, num_streams)

            current_logger_steps.append(f"Applied Model: {model_conf['name']};    New Video Resolution: {clip.width}x{clip.height}")

    final_resize_height = chain_conf.get("final_resize_height", 0)
    final_resize_factor = chain_conf.get("final_resize_factor", 100)

    if final_resize_height != 0 and final_resize_height != clip.height:
        clip = scale_to_1080(clip, round(final_resize_height * clip.width / clip.height), round(final_resize_height))
    elif final_resize_factor != 100:
        clip = vs.core.resize.Spline36(clip, width=clip.width * final_resize_factor / 100, height=clip.height * final_resize_factor / 100)

    if len(models) > 0:
        fmt_out = fmt_in
        if fmt_in not in [vs.YUV410P8, vs.YUV411P8, vs.YUV420P8, vs.YUV422P8, vs.YUV444P8, vs.YUV420P10, vs.YUV422P10,
                          vs.YUV444P10]:
            fmt_out = vs.YUV420P10

        clip = vs.core.resize.Spline36(clip, format=fmt_out, matrix_s=colorspace, range=1 if colorlv == 0 else None)

    if chain_conf['rife']:
        clip = rife_cuda.rife(
            clip,
            model=chain_conf['rife_model'],
            fps_in=float(container_fps),
            fps_num=chain_conf['rife_factor_numerator'],
            fps_den=chain_conf['rife_factor_denominator'],
            t_tta=chain_conf['rife_ensemble'],
            scene_detect_threshold=chain_conf['rife_scene_detect_threshold'],
            lt_d2k=True,
            tensorrt=backend.lower() == 'tensorrt'
        )
        current_logger_steps.append(f"Applied RIFE v{chain_conf['rife_model']} Interpolation {chain_conf['rife_factor_numerator'] / chain_conf['rife_factor_denominator']:.3f}x;    New Video FPS: {float(container_fps) * chain_conf['rife_factor_numerator'] / chain_conf['rife_factor_denominator']:.3f}")

    clip.set_output()


def run_animejanai_upscale(clip, backend, model_conf, trt_settings, num_streams):

    if model_conf['resize_height_before_upscale'] != 0 and model_conf['resize_height_before_upscale'] != clip.height:
        clip = scale_to_1080(clip, model_conf['resize_height_before_upscale'] * 16 / 9,
                             model_conf['resize_height_before_upscale'])
        current_logger_steps.append(f"Applied Resize Height Before Upscale: {model_conf['resize_height_before_upscale']}px;    New Video Resolution: {clip.width}x{clip.height}")

    elif clip.height > 1080:
        clip = scale_to_1080(clip)
        current_logger_steps.append(f"Applied Resize to Video Larger than 1080p;    New Video Resolution: {clip.width}x{clip.height}")

    # upscale 2x
    return upscale2x(clip, backend, model_conf['name'], num_streams, trt_settings)


# keybinding: 1-9
def run_animejanai_with_keybinding(clip, container_fps, keybinding):

    init()  # reload config so animejanai.conf edits apply without an mpv restart

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
        # try:
            # print(chain_conf['min_px'])
        # except:
        #     raise ValueError(f"{section_key} {config}")
        if chain_conf['min_px'] <= clip.width * clip.height <= chain_conf['max_px'] and \
                chain_conf['min_fps'] <= container_fps <= chain_conf['max_fps']:
            logger.debug(f'run_animejanai slot {keybinding} {chain_key}')

            current_logger_info.append(f"Active Upscale Chain: {chain_key.replace('chain_', '')};    Resolution Range: {chain_conf['min_resolution']} - {chain_conf['max_resolution']};    FPS Range: {chain_conf['min_fps']} - {chain_conf['max_fps']}")

            run_animejanai(clip, container_fps, chain_conf, config['global']['backend'])
            write_current_log()
            return

    current_logger_info.append("No Chains Activated")
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