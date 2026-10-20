#!/usr/bin/env bash
# Installs TurboDL for the current user (no sudo):
#   program   -> ~/.local/opt/turbodl/
#   launcher  -> ~/.local/share/applications/turbodl.desktop  (app grid / search)
#   command   -> ~/.local/bin/turbodl
set -euo pipefail
cd "$(dirname "$(readlink -f "$0")")"

SRC="publish"
APP="$HOME/.local/opt/turbodl"
BIN="$HOME/.local/bin"
APPS="$HOME/.local/share/applications"
ICONS="$HOME/.local/share/icons/hicolor"

[ -x "$SRC/TurboDL" ] || { echo "publish/TurboDL not found - run ./build-linux.sh first."; exit 1; }

if pgrep -x TurboDL >/dev/null 2>&1; then
  echo "Note: TurboDL is running. It keeps running the old version until you exit it (tray icon -> Exit)."
fi

# Replace atomically (a running copy keeps its old files open safely).
mkdir -p "$(dirname "$APP")"
rm -rf "$APP.new" "$APP.old"
cp -a "$SRC" "$APP.new"
[ -d "$APP" ] && mv "$APP" "$APP.old"
mv "$APP.new" "$APP"
rm -rf "$APP.old"

mkdir -p "$ICONS/256x256/apps" "$ICONS/64x64/apps" "$APPS" "$BIN"
cp Assets/turbodl.png "$ICONS/256x256/apps/turbodl.png"
cp Assets/turbodl-64.png "$ICONS/64x64/apps/turbodl.png"

cat > "$APPS/turbodl.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=TurboDL
GenericName=Download Manager
Comment=Fast multi-connection download manager
Exec="$APP/TurboDL" %U
Icon=turbodl
Terminal=false
Categories=Network;FileTransfer;
Keywords=download;manager;accelerator;idm;
StartupWMClass=TurboDL
StartupNotify=true
EOF
chmod 644 "$APPS/turbodl.desktop"

ln -sf "$APP/TurboDL" "$BIN/turbodl"

command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$ICONS" >/dev/null 2>&1 || true

echo "Installed. Open 'TurboDL' from the app grid (Super key, type TurboDL) or run: turbodl"
echo "If 'Start at login' was on, open TurboDL once so the autostart entry points to the new location."
