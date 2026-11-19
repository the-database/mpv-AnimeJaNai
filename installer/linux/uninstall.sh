#!/bin/sh
# Removes the desktop integration created by install.sh, and drops this install
# from the shared pack store (~/.local/share/the-database/aji): the TensorRT
# runtime no other installed app uses goes with it. The package tree itself is
# left in place - delete this folder to remove the rest.
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
if [ -x "$HERE/aji-runtime" ]; then
    "$HERE/aji-runtime" unref --app-id mpv-animejanai || true
    rm -f "$HERE/animejanai/inference/aji-runtime.json"
fi

BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

rm -f "$BIN/mpv-animejanai"
APP="$HERE/mpv-animejanai"
if [ -x "$APP" ] && [ "$(head -c 2 "$APP")" != "#!" ]; then
    # the launcher binary: entry, icon, and its default-player entries in mimeapps.list
    "$APP" --unregister desktop || true
else
    rm -f "$APPS/mpv-animejanai.desktop"
    rm -f "$ICONS/mpv-animejanai.png"
    update-desktop-database "$APPS" 2>/dev/null || true
fi

echo "Removed launcher symlink + desktop entry."
echo "Delete this folder to remove the player itself; user config under"
echo "  ~/.local/share/AnimeJaNai (AppImage) or this tree's portable_config remains."
