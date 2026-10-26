#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet build Toon.slnx --no-restore
(cd renderer && npm run typecheck)
if [ ! -e examples/acceptance-link ]; then
  ln -s ../contracts examples/acceptance-link
  trap 'rm -- examples/acceptance-link' EXIT
fi
dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll "$@"
