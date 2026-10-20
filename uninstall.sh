#!/usr/bin/env bash
# Removes TurboDL for the current user. Downloaded files are never touched.
#   ./uninstall.sh          keep settings and the download list (~/.local/share/TurboDL)
#   ./uninstall.sh --purge  remove those too
set -uo pipefail

pkill -x TurboDL 2>/dev/null && sleep 2   # SIGTERM: TurboDL saves progress and exits cleanly

rm -rf "$HOME/.local/opt/turbodl"
rm -f  "$HOME/.local/bin/turbodl"
rm -f  "$HOME/.local/share/applications/turbodl.desktop"
rm -f  "$HOME/.local/share/icons/hicolor/256x256/apps/turbodl.png" "$HOME/.local/share/icons/hicolor/64x64/apps/turbodl.png"
rm -f  "${XDG_CONFIG_HOME:-$HOME/.config}/autostart/turbodl.desktop"

if [ "${1:-}" = "--purge" ]; then
  rm -rf "$HOME/.local/share/TurboDL" "$HOME/snap/chromium/common/TurboDL-Extension"
  echo "TurboDL removed, including settings and the download list."
else
  echo "TurboDL removed. Settings and download list kept in ~/.local/share/TurboDL (use --purge to delete)."
fi
echo "Remove the browser extension from chrome://extensions if you no longer need it."
