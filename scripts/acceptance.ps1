param([switch]$Quick,[switch]$Library,[switch]$Characters,[switch]$Environments,[switch]$Voices,[switch]$Workflow)
$ErrorActionPreference = 'Stop'
if (([int]$Quick.IsPresent + [int]$Library.IsPresent + [int]$Characters.IsPresent + [int]$Environments.IsPresent + [int]$Voices.IsPresent + [int]$Workflow.IsPresent) -gt 1) { throw 'Choose one of -Quick, -Library, -Characters, -Environments, -Voices, -Workflow' }
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build Toon.slnx --no-restore
if ($LASTEXITCODE) { throw 'Build failed' }
Push-Location renderer
try { npm run typecheck; if ($LASTEXITCODE) { throw 'TypeScript failed' } } finally { Pop-Location }
$link = Join-Path $PWD 'examples/acceptance-link'
$madeLink = $false
try {
    if (-not (Test-Path -LiteralPath $link)) {
        New-Item -ItemType Junction -Path $link -Target (Join-Path $PWD 'contracts') | Out-Null
        $madeLink = $true
    }
    if ($Workflow) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --workflow }
    elseif ($Voices) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --voices }
    elseif ($Environments) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --environments }
    elseif ($Characters) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --characters }
    elseif ($Library) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --library }
    elseif ($Quick) { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll --quick }
    else { dotnet tests/Toon.Acceptance/bin/Debug/net10.0/Toon.Acceptance.dll }
    if ($LASTEXITCODE) { throw 'Acceptance failed; inspect printed report' }
} finally {
    # Nonrecursive unlink of the test junction we created, never its target.
    if ($madeLink -and (Get-Item -LiteralPath $link).LinkType -eq 'Junction') { [IO.Directory]::Delete($link) }
}
