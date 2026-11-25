#!/usr/bin/env bash
# Run VardyParty.LocalService from source (foreground, discovery HTTP ports).
# Self-locating: works regardless of the caller's current directory.
#
# Usage:
#   ./scripts/run-from-source.sh
#   ./scripts/run-from-source.sh --configuration Release
#   ./scripts/run-from-source.sh --no-build
# Extra args after -- are passed to `dotnet run`.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

PROJECT="$REPO_ROOT/VardyParty.LocalService/VardyParty.LocalService.csproj"
if [[ ! -f "$PROJECT" ]]; then
  echo "ERROR: project not found: $PROJECT" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "ERROR: dotnet SDK not found on PATH. Install .NET 11+ and retry." >&2
  exit 1
fi

CONFIGURATION="Debug"
NO_BUILD=0
NO_RESTORE=0
EXTRA_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      CONFIGURATION="${2:?missing configuration value}"
      shift 2
      ;;
    --no-build)
      NO_BUILD=1
      shift
      ;;
    --no-restore)
      NO_RESTORE=1
      shift
      ;;
    --)
      shift
      EXTRA_ARGS+=("$@")
      break
      ;;
    -h|--help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    *)
      EXTRA_ARGS+=("$1")
      shift
      ;;
  esac
done

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

# Load Auth0 (+ scrape-health allowlist) from repo-root .env — same keys packaging injects.
# Also mirror AUTH0_* → Auth0__* so IConfiguration binds Domain/Audience.
load_dotenv_auth0() {
  local env_file="$1"
  [[ -f "$env_file" ]] || return 0
  local keys=(
    AUTH0_DOMAIN
    AUTH0_AUDIENCE
    AUTH0_CLAIM_NAMESPACE
    AUTH0_REQUIREDROLECLAIMTYPE
    AUTH0_REQUIREDROLE
    AUTH0_REQUIRED_PERMISSION
    SCRAPE_HEALTH_ALLOWED_API_HOSTS
  )
  local key line value
  for key in "${keys[@]}"; do
    # Already set in the process environment wins.
    if [[ -n "${!key:-}" ]]; then
      continue
    fi
    line="$(grep -E "^[[:space:]]*${key}=" "$env_file" | tail -n1 || true)"
    [[ -n "$line" ]] || continue
    value="${line#*=}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    value="${value%\"}"
    value="${value#\"}"
    value="${value%\'}"
    value="${value#\'}"
    [[ -n "$value" ]] || continue
    export "$key=$value"
  done

  # ASP.NET Core configuration aliases (ResolveAuth0Settings also reads AUTH0_*).
  [[ -n "${AUTH0_DOMAIN:-}" && -z "${Auth0__Domain:-}" ]] && export Auth0__Domain="$AUTH0_DOMAIN"
  [[ -n "${AUTH0_AUDIENCE:-}" && -z "${Auth0__Audience:-}" ]] && export Auth0__Audience="$AUTH0_AUDIENCE"
  [[ -n "${AUTH0_CLAIM_NAMESPACE:-}" && -z "${Auth0__ClaimNamespace:-}" ]] && export Auth0__ClaimNamespace="$AUTH0_CLAIM_NAMESPACE"
  [[ -n "${AUTH0_REQUIREDROLECLAIMTYPE:-}" && -z "${Auth0__RequiredRoleClaimType:-}" ]] && export Auth0__RequiredRoleClaimType="$AUTH0_REQUIREDROLECLAIMTYPE"
  [[ -n "${AUTH0_REQUIREDROLE:-}" && -z "${Auth0__RequiredRole:-}" ]] && export Auth0__RequiredRole="$AUTH0_REQUIREDROLE"
  [[ -n "${AUTH0_REQUIRED_PERMISSION:-}" && -z "${Auth0__RequiredPermission:-}" ]] && export Auth0__RequiredPermission="$AUTH0_REQUIRED_PERMISSION"

  # Expand scrape-health CSV into indexed config keys (same as Import-VpAuth0Env).
  if [[ -n "${SCRAPE_HEALTH_ALLOWED_API_HOSTS:-}" ]]; then
    local idx=0
    local host_entry
    local indexed_var
    while true; do
      indexed_var="ScrapeHealth__AllowedApiHosts__${idx}"
      if [[ -z "${!indexed_var:-}" ]]; then
        break
      fi
      idx=$((idx + 1))
      [[ "$idx" -gt 256 ]] && break
    done
    IFS=',' read -ra _scrape_hosts <<< "$SCRAPE_HEALTH_ALLOWED_API_HOSTS"
    for host_entry in "${_scrape_hosts[@]}"; do
      host_entry="${host_entry#"${host_entry%%[![:space:]]*}"}"
      host_entry="${host_entry%"${host_entry##*[![:space:]]}"}"
      [[ -n "$host_entry" ]] || continue
      export "ScrapeHealth__AllowedApiHosts__${idx}=$host_entry"
      idx=$((idx + 1))
    done
  fi
}

load_dotenv_auth0 "$REPO_ROOT/.env"

# Fallback: LocalService user-secrets when .env still lacks Domain/Audience.
if [[ -z "${AUTH0_DOMAIN:-}" || -z "${AUTH0_AUDIENCE:-}" ]]; then
  if command -v dotnet >/dev/null 2>&1; then
    while IFS= read -r line; do
      key="${line%%=*}"
      value="${line#*=}"
      key="${key#"${key%%[![:space:]]*}"}"
      key="${key%"${key##*[![:space:]]}"}"
      value="${value#"${value%%[![:space:]]*}"}"
      value="${value%"${value##*[![:space:]]}"}"
      case "$key" in
        Auth0:Domain)
          [[ -z "${AUTH0_DOMAIN:-}" && -n "$value" ]] && export AUTH0_DOMAIN="$value" Auth0__Domain="$value"
          ;;
        Auth0:Audience)
          [[ -z "${AUTH0_AUDIENCE:-}" && -n "$value" ]] && export AUTH0_AUDIENCE="$value" Auth0__Audience="$value"
          ;;
        Auth0:ClaimNamespace)
          [[ -z "${AUTH0_CLAIM_NAMESPACE:-}" && -n "$value" ]] && export AUTH0_CLAIM_NAMESPACE="$value" Auth0__ClaimNamespace="$value"
          ;;
        Auth0:RequiredRoleClaimType)
          [[ -z "${AUTH0_REQUIREDROLECLAIMTYPE:-}" && -n "$value" ]] && export AUTH0_REQUIREDROLECLAIMTYPE="$value" Auth0__RequiredRoleClaimType="$value"
          ;;
        Auth0:RequiredRole)
          [[ -z "${AUTH0_REQUIREDROLE:-}" && -n "$value" ]] && export AUTH0_REQUIREDROLE="$value" Auth0__RequiredRole="$value"
          ;;
        Auth0:RequiredPermission)
          [[ -z "${AUTH0_REQUIRED_PERMISSION:-}" && -n "$value" ]] && export AUTH0_REQUIRED_PERMISSION="$value" Auth0__RequiredPermission="$value"
          ;;
      esac
    done < <(dotnet user-secrets list --project "$PROJECT" 2>/dev/null || true)
  fi
fi

RUN_ARGS=(
  run
  --project "$PROJECT"
  -c "$CONFIGURATION"
  --no-launch-profile
)
[[ "$NO_BUILD" -eq 1 ]] && RUN_ARGS+=(--no-build)
[[ "$NO_RESTORE" -eq 1 ]] && RUN_ARGS+=(--no-restore)
RUN_ARGS+=("${EXTRA_ARGS[@]}")

if [[ -n "${AUTH0_DOMAIN:-}" && -n "${AUTH0_AUDIENCE:-}" ]]; then
  AUTH0_STATE="Domain=${AUTH0_DOMAIN} Audience=NON_EMPTY"
else
  AUTH0_STATE="MISSING — set AUTH0_DOMAIN/AUTH0_AUDIENCE in .env or user-secrets on VardyParty.LocalService"
fi

echo "Running LocalService from source ($CONFIGURATION)…"
echo "  project: $PROJECT"
echo "  ASPNETCORE_ENVIRONMENT=$ASPNETCORE_ENVIRONMENT"
echo "  Auth0: $AUTH0_STATE"
echo "  HTTP: first free of 5019, 5137, 5273, 5417, 5573 (see /health)"
echo

exec dotnet "${RUN_ARGS[@]}"
