# Compatibility entry point for users who launch benchmark.ps1 directly.
# Windows and Linux use the same native-output timing implementation.
$ErrorActionPreference = 'Stop'
& "$PSScriptRoot\AnimeJaNaiBenchmark.exe" @args
exit $LASTEXITCODE
