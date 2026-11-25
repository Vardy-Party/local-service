#!/usr/bin/env bash
# Cloud Agent install script for the VardyParty Local Service.
#
# Idempotent: safe to run repeatedly and against cached/snapshotted state.
# Prepares everything needed to build, test, and run the ASP.NET Core service
# and its Playwright (Chromium) stream resolver on a headless Linux VM.
set -euo pipefail

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

SOLUTION="VardyParty.LocalService.slnx"
DOTNET_CHANNEL="11.0"
DOTNET_INSTALL_DIR="/usr/local/dotnet"

# ── 1. .NET 11 SDK ────────────────────────────────────────────────────────────
# Installed system-wide so `dotnet` is on PATH for every shell (no profile edits).
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q '^11\.'; then
  echo "==> Installing .NET ${DOTNET_CHANNEL} SDK to ${DOTNET_INSTALL_DIR}"
  tmp_script="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp_script"
  sudo mkdir -p "$DOTNET_INSTALL_DIR"
  sudo bash "$tmp_script" --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_INSTALL_DIR"
  sudo ln -sf "$DOTNET_INSTALL_DIR/dotnet" /usr/local/bin/dotnet
  rm -f "$tmp_script"
else
  echo "==> .NET 11 SDK already present: $(dotnet --version)"
fi

# ── 2. Restore + build (Release, matches CI) ─────────────────────────────────
echo "==> Restoring NuGet packages"
dotnet restore "$SOLUTION"

echo "==> Building solution (Release)"
dotnet build "$SOLUTION" -c Release --no-restore

# ── 3. Playwright Chromium + system dependencies ─────────────────────────────
# The service uses Microsoft.Playwright (Chromium headless) to resolve streams.
# The Playwright NuGet package ships a bundled Node CLI in the build output; use
# it to install the OS packages Chromium needs (apt, requires root) and to
# pre-download the browser so /play works without a first-run download.
PW_DIR="$REPO_ROOT/VardyParty.LocalService/bin/Release/net11.0/.playwright"
PW_NODE="$PW_DIR/node/linux-x64/node"
PW_CLI="$PW_DIR/package/cli.js"

# Match the writable browsers path the service resolves at runtime on Linux
# (see VardyParty.LocalService/Program.cs): $XDG_DATA_HOME or
# ~/.local/share, under VardyParty.LocalService/browsers.
BROWSERS_ROOT="${XDG_DATA_HOME:-$HOME/.local/share}/VardyParty.LocalService"
export PLAYWRIGHT_BROWSERS_PATH="$BROWSERS_ROOT/browsers"

if [[ -x "$PW_NODE" && -f "$PW_CLI" ]]; then
  echo "==> Installing Chromium system dependencies (apt)"
  sudo "$PW_NODE" "$PW_CLI" install-deps chromium

  echo "==> Installing Chromium browser to $PLAYWRIGHT_BROWSERS_PATH"
  mkdir -p "$PLAYWRIGHT_BROWSERS_PATH"
  "$PW_NODE" "$PW_CLI" install chromium
else
  echo "!! Playwright CLI not found under build output; skipping browser install." >&2
  echo "!! The service will attempt a first-run browser download instead." >&2
fi

echo "==> Install complete."
