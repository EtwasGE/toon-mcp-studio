#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet --version
node --version
dotnet restore Toon.slnx --locked-mode --packages .cache/nuget
(cd renderer && npm ci --cache ../.cache/npm --no-audit --no-fund && npm run browser && npm run typecheck)
dotnet build Toon.slnx --no-restore
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll doctor
