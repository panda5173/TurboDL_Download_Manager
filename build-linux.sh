#!/usr/bin/env bash
# Builds a PORTABLE TurboDL binary for Ubuntu / Linux x64 (no .NET install needed to run it).
#   ./build-linux.sh          -> publish/TurboDL  (+ publish/ChromeExtension)
set -uo pipefail
cd "$(dirname "$(readlink -f "$0")")"

fail() { echo; echo "BUILD FAILED - copy the errors above and share them."; exit 1; }

# ---------- .NET 10 SDK ----------
if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; fi

has_sdk10() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; }

if ! has_sdk10; then
  echo "=== .NET 10 SDK not found - installing it (one time) ==="
  if apt-cache show dotnet-sdk-10.0 >/dev/null 2>&1; then
    sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0 || true
  fi
  if ! has_sdk10; then
    echo "Ubuntu package not available - using Microsoft's installer into ~/.dotnet"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh || fail
    bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" || fail
    export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"
  fi
  has_sdk10 || fail
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# ---------- browser extension ----------
if [ ! -f ChromeExtension/manifest.json ]; then
  echo
  echo "WARNING: ChromeExtension/manifest.json not found."
  echo "         Copy the ChromeExtension folder from the Windows TurboDL project into this folder,"
  echo "         otherwise Settings -> Set up Chrome will not work. (The app itself builds fine.)"
  echo
fi

echo "=== Building PORTABLE TurboDL for Linux x64 (runs on Ubuntu 22.04+ / 24.04 / 26.04, no .NET needed) ==="
rm -rf publish
dotnet publish TurboDL.csproj -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false -o publish || fail

chmod +x publish/TurboDL
echo
echo "BUILD OK  ->  $(pwd)/publish/TurboDL"
echo "Next: ./install.sh   (adds TurboDL to the app grid; no sudo needed)"
