@echo off
rem Same benchmark runner on Windows and Linux. Default: native-display rendering.
"%~dp0AnimeJaNaiBenchmark.exe" %*
set "BENCHMARK_EXIT=%ERRORLEVEL%"
pause
exit /b %BENCHMARK_EXIT%
