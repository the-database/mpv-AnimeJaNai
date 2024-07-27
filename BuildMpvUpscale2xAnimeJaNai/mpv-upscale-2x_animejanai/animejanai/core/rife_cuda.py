# https://github.com/hooke007/MPV_lazy/blob/main/k7sfunc.py

from packaging.version import parse
import fractions
import math
import os
import typing
import vapoursynth as vs
from vapoursynth import core

vs_thd_init = os.cpu_count()
if 8 < vs_thd_init <= 16:
    vs_thd_dft = 8
elif vs_thd_init > 16:
    if vs_thd_init <= 32:
        vs_thd_dft = vs_thd_init // 2
        if vs_thd_dft % 2 != 0:
            vs_thd_dft = vs_thd_dft - 1
    else:
        vs_thd_dft = 16
else:
    vs_thd_dft = vs_thd_init

vsmlrt = None


def rife(
        input: vs.VideoNode,
        lt_d2k: bool = False,
        model: int = 414,
        ext_proc: bool = True,
        t_tta: bool = False,
        fps_in: float = 23.976,
        fps_num: int = 2,
        fps_den: int = 1,
        sc_mode: typing.Literal[0, 1, 2] = 1,
        gpu: typing.Literal[0, 1, 2] = 0,
        gpu_t: int = 2,
        st_eng: bool = False,
        ws_size: int = 0,
        vs_t: int = vs_thd_dft,
        scene_detect_threshold: float = 0.150,
        tensorrt: bool = True,
) -> vs.VideoNode:
    func_name = "RIFE_NV"
    if not isinstance(input, vs.VideoNode):
        raise vs.Error(f"模块 {func_name} 的子参数 input 的值无效")
    if not isinstance(lt_d2k, bool):
        raise vs.Error(f"模块 {func_name} 的子参数 lt_d2k 的值无效")
    if not isinstance(ext_proc, bool):
        raise vs.Error(f"模块 {func_name} 的子参数 ext_proc 的值无效")
    if not isinstance(t_tta, bool):
        raise vs.Error(f"模块 {func_name} 的子参数 t_tta 的值无效")
    if not isinstance(fps_in, (int, float)) or fps_in <= 0.0:
        raise vs.Error(f"模块 {func_name} 的子参数 fps_in 的值无效")
    if not isinstance(fps_num, int) or fps_num < 2:
        raise vs.Error(f"模块 {func_name} 的子参数 fps_num 的值无效")
    if not isinstance(fps_den, int) or fps_den >= fps_num or fps_num / fps_den <= 1:
        raise vs.Error(f"模块 {func_name} 的子参数 fps_den 的值无效")
    if sc_mode not in [0, 1, 2]:
        raise vs.Error(f"模块 {func_name} 的子参数 sc_mode 的值无效")
    if gpu not in [0, 1, 2]:
        raise vs.Error(f"模块 {func_name} 的子参数 gpu 的值无效")
    if not isinstance(gpu_t, int) or gpu_t <= 0:
        raise vs.Error(f"模块 {func_name} 的子参数 gpu_t 的值无效")
    if not isinstance(st_eng, bool):
        raise vs.Error(f"模块 {func_name} 的子参数 st_eng 的值无效")
    if not isinstance(ws_size, int) or ws_size < 0:
        raise vs.Error(f"模块 {func_name} 的子参数 ws_size 的值无效")
    if not isinstance(vs_t, int) or vs_t > vs_thd_init:
        raise vs.Error(f"模块 {func_name} 的子参数 vs_t 的值无效")

    if not hasattr(core, "trt"):
        raise ModuleNotFoundError(f"模块 {func_name} 依赖错误：缺失插件，检查项目 trt")
    if sc_mode == 1:
        if not hasattr(core, "misc"):
            raise ModuleNotFoundError(f"模块 {func_name} 依赖错误：缺失插件，检查项目 misc")
    elif sc_mode == 2:
        if not hasattr(core, "mv"):
            raise ModuleNotFoundError(f"模块 {func_name} 依赖错误：缺失插件，检查项目 mv")
    if not (fps_num / fps_den).is_integer():
        if not hasattr(core, "akarin"):
            raise ModuleNotFoundError(f"模块 {func_name} 依赖错误：缺失插件，检查项目 akarin")

    plg_dir = os.path.dirname(core.trt.Version()["path"]).decode()
    mdl_pname = "rife/" if ext_proc else "rife_v2/"

    mdl_str = str(model)
    mdl_fname_parts = [f"rife_v4.{mdl_str[1]}"]
    if len(mdl_str) == 4 and mdl_str[-1] == "1":
      mdl_fname_parts.append("lite")
    if t_tta:
      mdl_fname_parts.append("ensemble")

    mdl_fname = "_".join(mdl_fname_parts)

    mdl_pth = plg_dir + "/models/" + mdl_pname + mdl_fname + ".onnx"
    if not os.path.exists(mdl_pth):
        raise vs.Error(f"{func_name}: Model not found: {mdl_pth}")

    global vsmlrt
    if vsmlrt is None:
        try:
            import vsmlrt
        except ImportError:
            raise ImportError(f"模块 {func_name} 依赖错误：缺失脚本 vsmlrt")
    if parse(vsmlrt.__version__) < parse("3.18.22"):
        raise ImportError(f"模块 {func_name} 依赖错误：缺失脚本 vsmlrt 的版本号过低，至少 3.18.22")

    core.num_threads = vs_t
    w_in, h_in = input.width, input.height
    size_in = w_in * h_in
    colorlv = 1
    try:
        colorlv = getattr(input.get_frame(0).props, "_ColorRange", 0)
    except:
        pass
    fmt_in = input.format.id
    fps_factor = fps_num / fps_den

    if not ext_proc and model >= 47:  # https://github.com/AmusementClub/vs-mlrt/issues/72
        st_eng = True
    if (not lt_d2k and (size_in > 2048 * 1088)) or (size_in > 4096 * 2176):
        raise Exception(f"The source resolution {w_in}x{h_in} exceeds the limit and has been temporarily aborted.")
    if not st_eng and (((w_in > 4096) or (h_in > 2176)) or ((w_in < 289) or (h_in < 225))):
        raise Exception(f"The source resolution {w_in}x{h_in} is outside the range supported by the dynamic engine and has been temporarily discontinued.")

    scale_model = 1
    if lt_d2k and st_eng and (size_in > 2048 * 1088):
        scale_model = 0.5
        if not ext_proc:  # https://github.com/AmusementClub/vs-mlrt/blob/57cfe194fa8c21d221bdfaffebe4fee1af43d40c/scripts/vsmlrt.py#L903
            scale_model = 1
    if model >= 47:  # https://github.com/AmusementClub/vs-mlrt/blob/57cfe194fa8c21d221bdfaffebe4fee1af43d40c/scripts/vsmlrt.py#L895
        scale_model = 1

    tile_size = 32 / scale_model
    w_tmp = math.ceil(w_in / tile_size) * tile_size - w_in
    h_tmp = math.ceil(h_in / tile_size) * tile_size - h_in

    if sc_mode == 0:
        cut0 = input
    elif sc_mode == 1:
        cut0 = core.misc.SCDetect(clip=input, threshold=scene_detect_threshold)
    elif sc_mode == 2:
        sup = core.mv.Super(clip=input, pel=1)
        vec = core.mv.Analyse(super=sup, isb=True)
        cut0 = core.mv.SCDetection(clip=input, vectors=vec, thscd1=240, thscd2=130)

    try:
        cut1 = core.resize.Bilinear(clip=cut0, format=vs.RGBH, matrix_in_s="709")
        return rife_cut(cut1, ext_proc, w_tmp, h_tmp, tensorrt, t_tta, fps_num, fps_den, scale_model, model, gpu_t,
                        ws_size, st_eng, lt_d2k, gpu, fps_factor, colorlv, fps_in, fmt_in)
    except:
        cut1 = core.resize.Bilinear(clip=cut0, format=vs.RGBS, matrix_in_s="709")
        return rife_cut(cut1, ext_proc, w_tmp, h_tmp, tensorrt, t_tta, fps_num, fps_den, scale_model, model, gpu_t,
                        ws_size, st_eng, lt_d2k, gpu, fps_factor, colorlv, fps_in, fmt_in)


def rife_cut(cut1, ext_proc, w_tmp, h_tmp, tensorrt, t_tta, fps_num, fps_den, scale_model, model, gpu_t, ws_size, st_eng, lt_d2k, gpu, fps_factor, colorlv, fps_in, fmt_in):
    if ext_proc:
        if w_tmp + h_tmp > 0:
            cut1 = core.std.AddBorders(clip=cut1, right=w_tmp, bottom=h_tmp)
        if tensorrt:
            fin = vsmlrt.RIFE(clip=cut1, multi=fractions.Fraction(fps_num, fps_den), scale=scale_model, model=model,
                              ensemble=t_tta, _implementation=1, video_player=True, backend=vsmlrt.BackendV2.TRT(
                    num_streams=gpu_t, force_fp16=True, output_format=1,
                    workspace=None if ws_size < 128 else (ws_size if st_eng else ws_size * 2),
                    use_cuda_graph=True, use_cublas=False, use_cudnn=False,
                    static_shape=st_eng, min_shapes=[0, 0] if st_eng else [320, 256],
                    opt_shapes=None if st_eng else [1920, 1088],
                    max_shapes=None if st_eng else ([4096, 2176] if lt_d2k else [2048, 1088]),
                    device_id=gpu, short_path=True))
        else:
            fin = vsmlrt.RIFE(clip=cut1, multi=fractions.Fraction(fps_num, fps_den), scale=scale_model, model=model,
                              ensemble=t_tta, _implementation=1, video_player=True, backend=vsmlrt.BackendV2.ORT_DML(
                                fp16=True, device_id=gpu
                ))
        if w_tmp + h_tmp > 0:
            fin = core.std.Crop(clip=fin, right=w_tmp, bottom=h_tmp)
    else:
        if tensorrt:
            fin = vsmlrt.RIFE(clip=cut1, multi=fractions.Fraction(fps_num, fps_den), scale=scale_model, model=model,
                              ensemble=t_tta, _implementation=2, video_player=True, backend=vsmlrt.BackendV2.TRT(
                    num_streams=gpu_t, force_fp16=True, output_format=1,
                    workspace=None if ws_size < 128 else ws_size,
                    use_cuda_graph=True, use_cublas=False, use_cudnn=False,
                    static_shape=st_eng, min_shapes=[0, 0],
                    opt_shapes=None, max_shapes=None,
                    device_id=gpu, short_path=True))
        else:
            fin = vsmlrt.RIFE(clip=cut1, multi=fractions.Fraction(fps_num, fps_den), scale=scale_model, model=model,
                              ensemble=t_tta, _implementation=2, video_player=True, backend=vsmlrt.BackendV2.ORT_DML(
                    fp16=True, device_id=gpu
                ))
    output = core.resize.Bilinear(clip=fin, format=fmt_in, matrix_s="709", range=1 if colorlv == 0 else None)
    if not fps_factor.is_integer():
        output = core.std.AssumeFPS(clip=output, fpsnum=fps_in * fps_num * 1e6, fpsden=fps_den * 1e6)

    return output