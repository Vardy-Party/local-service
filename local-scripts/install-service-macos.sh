#!/usr/bin/env bash
set -euo pipefail

SERVICE_LABEL="com.vardyparty.localservice"
# Resolve to an absolute, symlink-free path: the unit/plist is read later from
# another working directory. `cd && pwd -P` works on macOS bash 3.2 (no realpath).
if [[ $# -ge 1 ]]; then
  if [[ ! -d "$1" ]]; then
    echo "Publish directory not found: $1" >&2
    exit 1
  fi
  PUBLISH_DIR="$(CDPATH= cd -- "$1" && pwd -P)"
else
  PUBLISH_DIR="$(CDPATH= cd -- "$(dirname "$0")/../VardyParty.LocalService/bin/Release/net11.0/publish" && pwd -P)"
fi

DLL_PATH="$PUBLISH_DIR/VardyParty.LocalService.dll"
APPHOST_PATH="$PUBLISH_DIR/VardyParty.LocalService"
PLIST_PATH="$HOME/Library/LaunchAgents/${SERVICE_LABEL}.plist"

RUNTIMECONFIG_PATH="$PUBLISH_DIR/VardyParty.LocalService.runtimeconfig.json"

# XML-escape a value for a plist <string> (& first, then < > " ').
# sed rather than ${v//x/y}: bash 5.2 treats & in the replacement as the match.
plist_escape() {
  printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' \
    -e 's/"/\&quot;/g' -e "s/'/\\&apos;/g"
}

# A self-contained publish (the release tarballs) bundles the runtime: its
# runtimeconfig.json lists "includedFrameworks" and libcoreclr.dylib sits next
# to the dll. Only then start the native apphost, which needs no .NET install.
# A framework-dependent publish (plain `dotnet publish -c Release`) also has an
# apphost, but that one has to locate a shared runtime, which often fails under a
# service manager when dotnet lives somewhere non-standard; run `dotnet <dll>`.
SELF_CONTAINED=false
if [[ -f "$PUBLISH_DIR/libcoreclr.dylib" ]] || { [[ -f "$RUNTIMECONFIG_PATH" ]] && grep -q '"includedFrameworks"' "$RUNTIMECONFIG_PATH"; }; then
  SELF_CONTAINED=true
fi

if [[ "$SELF_CONTAINED" == true ]]; then
  if [[ ! -x "$APPHOST_PATH" ]]; then
    echo "Self-contained publish in $PUBLISH_DIR but $APPHOST_PATH is missing or not executable"
    echo "If it exists, run: chmod +x \"$APPHOST_PATH\""
    exit 1
  fi
  PROGRAM_ARGS="    <string>$(plist_escape "$APPHOST_PATH")</string>"
elif [[ -f "$DLL_PATH" ]]; then
  DOTNET_PATH="$(command -v dotnet || true)"
  if [[ -z "$DOTNET_PATH" ]]; then
    echo "dotnet not found in PATH (needed for the framework-dependent publish in $PUBLISH_DIR)"
    exit 1
  fi
  # A relative PATH entry makes `command -v` return a relative path; the service
  # manager would resolve that against its own directory, so make it absolute.
  if [[ "$DOTNET_PATH" != /* ]]; then
    DOTNET_PATH="$(CDPATH= cd -- "$(dirname "$DOTNET_PATH")" && pwd -P)/$(basename "$DOTNET_PATH")"
  fi
  PROGRAM_ARGS="    <string>$(plist_escape "$DOTNET_PATH")</string>
    <string>$(plist_escape "$DLL_PATH")</string>"
else
  echo "Could not find a self-contained publish or $DLL_PATH in $PUBLISH_DIR"
  echo "Publish first: dotnet publish ../VardyParty.LocalService/VardyParty.LocalService.csproj -c Release"
  exit 1
fi

mkdir -p "$(dirname "$PLIST_PATH")"

cat > "$PLIST_PATH" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$(plist_escape "$SERVICE_LABEL")</string>
  <key>ProgramArguments</key>
  <array>
$PROGRAM_ARGS
  </array>
  <key>WorkingDirectory</key>
  <string>$(plist_escape "$PUBLISH_DIR")</string>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>StandardOutPath</key>
  <string>$(plist_escape "$HOME/Library/Logs/vardyparty-local-service.out.log")</string>
  <key>StandardErrorPath</key>
  <string>$(plist_escape "$HOME/Library/Logs/vardyparty-local-service.err.log")</string>
</dict>
</plist>
EOF

launchctl bootout "gui/$(id -u)/$SERVICE_LABEL" >/dev/null 2>&1 || true
launchctl bootstrap "gui/$(id -u)" "$PLIST_PATH"
launchctl enable "gui/$(id -u)/$SERVICE_LABEL"
launchctl kickstart -k "gui/$(id -u)/$SERVICE_LABEL"

echo "Installed and started launchd agent '$SERVICE_LABEL'"
launchctl print "gui/$(id -u)/$SERVICE_LABEL" | head -n 30
