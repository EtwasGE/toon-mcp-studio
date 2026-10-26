$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
dotnet --version
node --version
dotnet restore Toon.slnx --locked-mode --packages .cache/nuget
if ($LASTEXITCODE) { throw 'NuGet restore failed' }
Push-Location renderer
try {
    npm ci --cache ../.cache/npm --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    npm run browser
    if ($LASTEXITCODE) { throw 'Chromium installation failed' }
    npm run typecheck
    if ($LASTEXITCODE) { throw 'TypeScript check failed' }
} finally { Pop-Location }
dotnet build Toon.slnx --no-restore
if ($LASTEXITCODE) { throw 'Build failed' }
dotnet src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll doctor
