@echo off
:: Note: This bat script must be executed using CMD command prompt and not PowerShell.
:: You must have ffmpeg installed and available on your path: https://www.wikihow.com/Install-FFmpeg-on-Windows

:: Input and output video settings. Edit input_video to the path of the video you want to upscale,
:: and edit output_video to the path where you want the upscaled video to be created.
:: The paths must use / or \\ between the folder names, but not \. 
set input_video=<local path>
set output_video=<local path>

:: Video compression settings. Remove the :: from the line with video_settings you want to use, 
:: add :: to the beginning of lines with video_settings you don't want to use, and modify any settings 
:: to adjust video quality, compression level, and encoding speed as needed.
:: https://ffmpeg.org/ffmpeg-codecs.html#Video-Encoders

:: FFV1 lossless compression, fastest to encode but extremely large filesize
:: set video_settings=ffv1

:: x265 compression (https://ffmpeg.org/ffmpeg-codecs.html#libx265), more efficient compression but slower to encode
:: set video_settings=libx265 -crf 16 -preset slow -x265-params "sao=0:bframes=8:psy-rd=1.5:psy-rdoq=2:aq-mode=3:ref=6"

:: HEVC compression with NVENC, fast encode using GPU but lower quality than CPU encoding
set video_settings=hevc_nvenc -preset p7 -profile:v main10 -b:v 50M

:: x264 compression (https://ffmpeg.org/ffmpeg-codecs.html#libx264_002c-libx264rgb), OK compression, faster to encode
:: set video_settings=libx264 -crf 13 -preset slow -tune animation

:: Upscaling slot to use. Slots are configured in animejanai_v2.conf. 
set upscale_slot=1

..\..\VSPipe.exe -c y4m --arg "slot=%upscale_slot%" --arg "video_path=%input_video%" ./animejanai_v2_encode.vpy - | ffmpeg -i pipe: -i "%input_video%" -map 0 -map 1:a -map 1:s -c:v %video_settings% "%output_video%"
