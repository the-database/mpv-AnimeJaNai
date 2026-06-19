#!/bin/sh
# AnimeJaNai (Linux portable) installer.
#
# Run from inside the extracted package directory: `./install.sh`. It does NOT
# move the package - it registers the in-place tree with your desktop:
#   - a launcher symlink in ~/.local/bin
#   - a .desktop menu entry (~/.local/share/applications)
#   - video MIME associations (xdg-mime)
# The tree stays writable, so the in-place updater (Ctrl+U) keeps working.
# Uninstall with ./uninstall.sh (the tree itself is removed by deleting it).
set -eu

HERE="$(cd "$(dirname "$0")" && pwd)"
APP="$HERE/mpv-animejanai"
BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICONS="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/256x256/apps"

if [ ! -x "$APP" ]; then
    echo "error: launcher not found at $APP (run this from the extracted package)" >&2
    exit 1
fi

mkdir -p "$BIN" "$APPS" "$ICONS"

ln -sf "$APP" "$BIN/mpv-animejanai"

icon_line="Icon=mpv-animejanai"
if [ -f "$HERE/animejanai.png" ]; then
    cp -f "$HERE/animejanai.png" "$ICONS/mpv-animejanai.png"
elif [ -f "$HERE/portable_config/animejanai.png" ]; then
    cp -f "$HERE/portable_config/animejanai.png" "$ICONS/mpv-animejanai.png"
else
    icon_line="Icon=mpv"
fi

MIMES="video/x-matroska;video/mp4;video/webm;video/x-msvideo;video/quicktime;video/mpeg;video/x-flv;video/3gpp;"

cat > "$APPS/mpv-animejanai.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=mpv AnimeJaNai
GenericName=Media Player
Comment=Real-time anime upscaling video player (mpv + TensorRT)
Exec=$APP %U
TryExec=$APP
$icon_line
Terminal=false
Categories=AudioVideo;Player;Video;
MimeType=$MIMES
StartupNotify=true
EOF

update-desktop-database "$APPS" 2>/dev/null || true

for m in $(echo "$MIMES" | tr ';' ' '); do
    [ -n "$m" ] && xdg-mime default mpv-animejanai.desktop "$m" 2>/dev/null || true
done

echo "Installed:"
echo "  launcher : $BIN/mpv-animejanai  ->  $APP"
echo "  menu     : $APPS/mpv-animejanai.desktop"
echo
case ":$PATH:" in
    *":$BIN:"*) ;;
    *) echo "NOTE: $BIN is not on your PATH; add it to use 'mpv-animejanai' from a shell." ;;
esac
echo "Launch 'mpv AnimeJaNai' from your application menu, or: $APP <file>"
