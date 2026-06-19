#!/bin/sh
# Linux analogue of animejanai_benchmark_all.bat: runs the cross-platform
# AnimeJaNaiBenchmark tool against this install's bundled clips + benchmark slots.
HERE="$(cd "$(dirname "$0")" && pwd)"   # animejanai/benchmarks
ROOT="$(cd "$HERE/../.." && pwd)"       # install root
# bundled mpv + inference libs on the loader path
export LD_LIBRARY_PATH="$ROOT/mpv:$ROOT/animejanai/inference:${LD_LIBRARY_PATH}"
exec "$HERE/AnimeJaNaiBenchmark" --install-root "$ROOT" "$@"
echo
read -p "Press Enter to close..." _
