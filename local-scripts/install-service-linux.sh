#!/usr/bin/env bash
set -euo pipefail

SERVICE_NAME="vardyparty-local-service"
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
UNIT_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

RUNTIMECONFIG_PATH="$PUBLISH_DIR/VardyParty.LocalService.runtimeconfig.json"

# systemd unit values are not shell words (systemd.service(5), systemd.unit(5)).
# A unit line cannot hold a newline, so refuse those paths outright.
# - ExecStart= splits on spaces and honours "double quotes" with C escapes and
#   %specifiers, so every word is quoted and % doubled. Arguments also get \ and
#   " escaped and $ doubled ($VARS expand only in arguments). systemd rejects a
#   program path containing ", ', \ or a control character (tab, ...) even when
#   escaped, so such a native exe or dotnet path is refused before any unit is
#   written. Arguments and WorkingDirectory= accept tabs.
# - WorkingDirectory= takes the rest of the line as one path (quotes would be
#   literal), so only % specifiers are escaped.
systemd_reject_newline() {
  if [[ "$1" == *$'\n'* || "$1" == *$'\r'* ]]; then
    echo "Path contains a newline, which a systemd unit cannot represent: $1" >&2
    exit 1
  fi
}

# Call in the main shell (not inside $(...)) so the error stops the script.
systemd_check_program() {
  systemd_reject_newline "$1"
  case "$1" in
    *\"*|*\'*|*\\*)
      echo "Program path contains \", ' or \\, which systemd does not accept in ExecStart=: $1" >&2
      echo "Move the publish (or dotnet) to a path without those characters." >&2
      exit 1
      ;;
    *[[:cntrl:]]*)
      echo "Program path contains a control character (e.g. a tab), which systemd does not accept in ExecStart=: $(printf '%q' "$1")" >&2
      echo "Move the publish (or dotnet) to a path without control characters." >&2
      exit 1
      ;;
  esac
}

systemd_exec_path() {
  local value="$1"
  printf '"%s"' "${value//%/%%}"
}

systemd_exec_arg() {
  local value="$1"
  systemd_reject_newline "$value"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//%/%%}"
  value="${value//\$/\$\$}"
  printf '"%s"' "$value"
}

systemd_path() {
  local value="$1"
  systemd_reject_newline "$value"
  printf '%s' "${value//%/%%}"
}

# A self-contained publish (the release tarballs) bundles the runtime: its
# runtimeconfig.json lists "includedFrameworks" and libcoreclr.so sits next to the
# dll. Only then start the native apphost, which needs no .NET install.
# A framework-dependent publish (plain `dotnet publish -c Release`) also has an
# apphost, but that one has to locate a shared runtime, which often fails under a
# service manager when dotnet lives somewhere non-standard; run `dotnet <dll>`.
SELF_CONTAINED=false
if [[ -f "$PUBLISH_DIR/libcoreclr.so" ]] || { [[ -f "$RUNTIMECONFIG_PATH" ]] && grep -q '"includedFrameworks"' "$RUNTIMECONFIG_PATH"; }; then
  SELF_CONTAINED=true
fi

if [[ "$SELF_CONTAINED" == true ]]; then
  if [[ ! -x "$APPHOST_PATH" ]]; then
    echo "Self-contained publish in $PUBLISH_DIR but $APPHOST_PATH is missing or not executable"
    echo "If it exists, run: chmod +x \"$APPHOST_PATH\""
    exit 1
  fi
  systemd_check_program "$APPHOST_PATH"
  EXEC_START="$(systemd_exec_path "$APPHOST_PATH")"
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
  systemd_check_program "$DOTNET_PATH"
  systemd_reject_newline "$DLL_PATH"
  EXEC_START="$(systemd_exec_path "$DOTNET_PATH") $(systemd_exec_arg "$DLL_PATH")"
else
  echo "Could not find a self-contained publish or $DLL_PATH in $PUBLISH_DIR"
  echo "Publish first: dotnet publish ../VardyParty.LocalService/VardyParty.LocalService.csproj -c Release"
  exit 1
fi

systemd_reject_newline "$PUBLISH_DIR"
WORKING_DIRECTORY="$(systemd_path "$PUBLISH_DIR")"

sudo tee "$UNIT_FILE" >/dev/null <<EOF
[Unit]
Description=VardyParty Local Service
After=network.target

[Service]
Type=simple
WorkingDirectory=$WORKING_DIRECTORY
ExecStart=$EXEC_START
Restart=always
RestartSec=5
Environment=ASPNETCORE_ENVIRONMENT=Production

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable "$SERVICE_NAME"
sudo systemctl restart "$SERVICE_NAME"

echo "Installed and started systemd service '$SERVICE_NAME'"
sudo systemctl status "$SERVICE_NAME" --no-pager
