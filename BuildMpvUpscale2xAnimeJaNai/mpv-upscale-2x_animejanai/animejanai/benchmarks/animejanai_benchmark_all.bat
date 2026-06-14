@echo off
rem AnimeJaNai playback benchmark (launched by the config editor's
rem Benchmark button). Drives real mpv playback across the
rem bundled source clips on the configured backend and writes
rem benchmark.txt next to animejanai.conf.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0benchmark.ps1"
pause
