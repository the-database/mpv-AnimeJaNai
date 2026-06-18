#!/bin/bash
# Builds the AnimeJaNai AppImage from a *full* assembled linux-x64 tree (the
# AppImage can't download component packs into its read-only squashfs, so the
# TensorRT runtime + per-GPU builder resources + RIFE models must already be in
# the tree). Writable runtime data is redirected to $XDG_DATA_HOME/AnimeJaNai by
# AppRun.
#
# Usage: build-appimage.sh <full-tree-dir> <version> [out-dir]
#
# linuxdeploy gathers mpv's shared-lib deps; the AppImage excludelist keeps the
# host-provided graphics stack out (libvulkan, libGL, libwayland, glibc, …).
# Runs headless via APPIMAGE_EXTRACT_AND_RUN (no FUSE needed on CI runners).
set -euo pipefail

TREE="$1"
VER="$2"
OUT="${3:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$(mktemp -d)"
APPDIR="$WORK/AppDir"
export APPIMAGE_EXTRACT_AND_RUN=1
export ARCH=x86_64

[ -d "$TREE" ] || { echo "tree not found: $TREE" >&2; exit 1; }

# --- tools (download the single-file AppImage tools if not on PATH) --------
tooldir="$WORK/tools"; mkdir -p "$tooldir"
fetch() { # url dest
    if ! command -v "$(basename "$2")" >/dev/null 2>&1; then
        curl -fsSL "$1" -o "$2/$(basename "$1")"; chmod +x "$2/$(basename "$1")"
        echo "$2/$(basename "$1")"
    else command -v "$(basename "$2")"; fi
}
LD=$(fetch "https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage" "$tooldir")
AT=$(fetch "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" "$tooldir")

# --- assemble AppDir -------------------------------------------------------
mkdir -p "$APPDIR/usr/lib" "$APPDIR/usr/bin"
cp -a "$TREE/." "$APPDIR/"
install -m755 "$HERE/AppRun" "$APPDIR/AppRun"

# icon: reuse one from the tree if present, else generate a placeholder
icon="$APPDIR/mpv-animejanai.png"
if   [ -f "$TREE/portable_config/animejanai.png" ]; then cp "$TREE/portable_config/animejanai.png" "$icon"
elif [ -f "$HERE/animejanai.png" ]; then cp "$HERE/animejanai.png" "$icon"
elif command -v convert >/dev/null 2>&1; then
    # plain solid square — no text, so it needs no font (containers often lack one)
    convert -size 256x256 xc:'#1b1033' "$icon"
else
    # 1x1 transparent PNG fallback (appimagetool just needs a valid icon file)
    printf '\211PNG\r\n\032\n\0\0\0\rIHDR\0\0\0\1\0\0\0\1\10\6\0\0\0\37\25\304\211\0\0\0\rIDATx\234c\374\17\0\0\1\1\0\5\030\335\215\260\0\0\0\0IEND\256B`\202' > "$icon"
fi

cat > "$APPDIR/mpv-animejanai.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=mpv AnimeJaNai
GenericName=Media Player
Comment=Real-time anime upscaling video player (mpv + TensorRT)
Exec=AppRun %U
Icon=mpv-animejanai
Terminal=false
Categories=AudioVideo;Player;Video;
MimeType=video/x-matroska;video/mp4;video/webm;video/x-msvideo;video/quicktime;video/mpeg;
EOF

# --- bundle mpv's deps (excludelist keeps host graphics libs out) ----------
# Point linuxdeploy at the real mpv binary; it walks NEEDED and copies the
# non-excluded libs into usr/lib. The bundled libmpv/libplacebo/ffmpeg already
# sit in mpv/ (on AppRun's LD_LIBRARY_PATH), so this mainly captures the smaller
# system deps (libass, freetype, fribidi, harfbuzz, lcms2, …).
"$LD" --appdir "$APPDIR" \
    --executable "$APPDIR/mpv/mpv" \
    --library "$APPDIR/mpv/libmpv.so.2" \
    --desktop-file "$APPDIR/mpv-animejanai.desktop" \
    --icon-file "$icon" || true

# linuxdeploy may install its own AppRun; restore ours (we need the
# write-redirection logic, not a plain exec wrapper).
install -m755 "$HERE/AppRun" "$APPDIR/AppRun"

# --- package ---------------------------------------------------------------
mkdir -p "$OUT"
outfile="$OUT/mpv-AnimeJaNai-x86_64-v${VER}.AppImage"
"$AT" "$APPDIR" "$outfile"
echo "$outfile"
rm -rf "$WORK"
