#!/bin/sh
# Packages an assembled linux-x64 install tree into the portable tarball:
# drops the updater + install/uninstall scripts at the tree root, then tars
# the tree (zstd) with a versioned top-level directory.
#
# Usage: package-tarball.sh <tree-dir> <updater-binary> <version> [out-dir]
# The install.sh / uninstall.sh shipped are the ones next to this script.
set -eu

TREE="$1"
UPDATER="$2"
VER="$3"
OUT="${4:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"

[ -d "$TREE" ] || { echo "tree dir not found: $TREE" >&2; exit 1; }
[ -f "$UPDATER" ] || { echo "updater not found: $UPDATER" >&2; exit 1; }

cp -f "$UPDATER" "$TREE/AnimeJaNaiUpdater"
chmod +x "$TREE/AnimeJaNaiUpdater"
cp -f "$HERE/install.sh" "$HERE/uninstall.sh" "$TREE/"
chmod +x "$TREE/install.sh" "$TREE/uninstall.sh" "$TREE/mpv-animejanai" 2>/dev/null || true

mkdir -p "$OUT"
name="mpv-upscale-2x_animejanai-v${VER}-linux-x64.tar.zst"
# top-level dir in the archive = the tree's own (versioned) basename
tar --zstd -C "$(dirname "$TREE")" -cf "$OUT/$name" "$(basename "$TREE")"
echo "$OUT/$name"
