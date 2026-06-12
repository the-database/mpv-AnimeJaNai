@echo off
rem AnimeJaNai inference benchmark (launched by the config editor's
rem Benchmark button). Drives the native inference harness across the
rem bundled seed resolutions on the configured backend and writes
rem benchmark.txt next to animejanai.conf.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0benchmark.ps1"
pause
