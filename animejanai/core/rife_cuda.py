# https://github.com/hooke007/MPV_lazy/blob/main/portable_config/vs/rife_cuda.vpy
# https://github.com/vadash/mpv-lazy-en/blob/main/portable_config/vs/rife_cuda.vpy
### RIFE 4.6 TRT for Nvidia RTX 2000+

import vapoursynth as vs
from vapoursynth import core
import math
from vsmlrt import RIFE, RIFEModel, Backend

def rife(clip, clip_dw, clip_dh, container_fps):
    colorlv = clip.get_frame(0).props._ColorRange
    fmt_fin = clip.format.id

    interpMulti = 2 			# frame rate (integer)
    if clip_dh <= 720: interpMulti = 6
    elif clip_dh <= 1080: interpMulti = 2
    GPU = 0 							# The serial number of the graphics card used, 0 is sorted number one
    GPU_t = 3 						# Number of graphics card threads used
    WS_size = 2048        # Constrained video memory size
    maxIpps = 80000000    # Max interpolated pixels per second the GPU is capable of. 80kk is solid starting point for 3070ti providing 80% CUDA load
    if interpMulti == 3: maxIpps = 1.2 * maxIpps # approximation fix
    elif interpMulti == 6: maxIpps = 1.4 * maxIpps # approximation fix

    if container_fps > 59 :
        raise Exception("The source frame rate exceeds the limit and the script has been temporarily disabled")

    # scale video down (if we cant play it in source quality)
    dsWidth = clip_dw
    dsHeight = clip_dh
    clipIpps = container_fps * clip_dw * clip_dh * (interpMulti - 1)
    dsPercent = 1.00
    while clipIpps > maxIpps:
      dsPercent -= 0.01
      dsWidth = math.ceil(clip_dw * dsPercent)
      dsHeight = math.ceil(clip_dh * dsPercent)
      clipIpps = container_fps * dsWidth * dsHeight * (interpMulti - 1)
    if dsPercent < 0.95:
      dsWidth = math.ceil(dsWidth / 32) * 32
      dsHeight = math.ceil(dsHeight / 32) * 32
      clip = core.resize.Spline36(clip, width=dsWidth, height=dsHeight, format=vs.RGBS, matrix_in_s="709")
    else:
      clip = core.resize.Bilinear(clip, format=vs.RGBS, matrix_in_s="709")

    # RIFE requires 32x32 blocks
    w_tmp = math.ceil(clip.width / 32) * 32 - clip.width
    h_tmp = math.ceil(clip.height / 32) * 32 - clip.height
    clip = core.misc.SCDetect(clip, threshold=0.2)
    clip = clip.std.AddBorders(right=w_tmp, bottom=h_tmp)
    clip = RIFE(clip=clip, multi=interpMulti, model=RIFEModel.v4_6, backend=Backend.TRT(fp16=True, device_id=GPU, workspace=WS_size, use_cuda_graph=True, num_streams=GPU_t))
    clip = clip.std.Crop(right=w_tmp, bottom=h_tmp)
    clip = clip.resize.Bilinear(format=fmt_fin, matrix_s="709", range=1 if colorlv==0 else None)
    return clip
