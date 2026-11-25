#!/usr/bin/env bash
# Cloud-agent / Cursor terminal entrypoint — delegates to the shared from-source runner.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec bash "$REPO_ROOT/scripts/run-from-source.sh" --configuration Release "$@"
