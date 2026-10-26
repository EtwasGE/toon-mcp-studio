#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[a-z][a-z0-9-]{0,39}$')][string]$Profile = 'default',
    [string]$DataRoot,
    [string[]]$ImportRoots,
    [ValidateRange(0, 100000)][int]$TtsLimit = 0,
    [switch]$NoConnectCodex,
    [switch]$Setup,
    [string]$Brief,
    [string]$EpisodeKey,
    [string]$ProjectId,
    [int]$Revision = 1,
    [switch]$Preview,
    [ValidateRange(0, 100000)][int]$TtsCharacters = 0,
    [string]$JobId,
    [switch]$Open
)
$ErrorActionPreference = 'Stop'
$toonRoot = Split-Path $PSScriptRoot -Parent
if ($Setup) {
    & (Join-Path $PSScriptRoot 'setup.ps1')
}
$cli = Join-Path $toonRoot 'src/Toon.Cli/bin/Debug/net10.0/Toon.Cli.dll'
$worker = Join-Path $toonRoot 'src/Toon.Worker/bin/Debug/net10.0/Toon.Worker.dll'
$mcp = Join-Path $toonRoot 'src/Toon.Mcp/bin/Debug/net10.0/Toon.Mcp.dll'
foreach ($required in @($cli, $worker, $mcp, (Join-Path $toonRoot 'renderer/node_modules/@remotion/cli/remotion-cli.js'))) {
    if (!(Test-Path -LiteralPath $required)) {
        throw 'Dependencies are missing. Run ./scripts/studio.ps1 -Setup first.'
    }
}
$profileDirectory = Join-Path $toonRoot '.data/studio-profiles'
New-Item -ItemType Directory -Path $profileDirectory -Force | Out-Null
$profilePath = Join-Path $profileDirectory "$Profile.json"
$saved = if (Test-Path -LiteralPath $profilePath) { Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json } else { $null }
if (!$DataRoot) {
    $DataRoot = if ($saved) { $saved.data_root } elseif ($Profile -eq 'alkash') { Join-Path $toonRoot '.data/codilya-reference/runtime' } elseif ($Profile -eq 'default') { Join-Path $toonRoot '.data' } else { Join-Path $toonRoot ".data/workspaces/$Profile" }
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot, $toonRoot)
if (!$ImportRoots) {
    $ImportRoots = if ($saved) { @($saved.import_roots) } elseif ($Profile -eq 'alkash') { @((Join-Path $toonRoot '.data/codilya-reference')) } else { @((Join-Path $toonRoot 'examples')) }
}
$ImportRoots = @($ImportRoots | ForEach-Object { [IO.Path]::GetFullPath($_, $toonRoot) })
if (!$PSBoundParameters.ContainsKey('TtsLimit') -and $saved) {
    $TtsLimit = $saved.tts_character_limit
}
if ($TtsCharacters -gt $TtsLimit) {
    throw 'The requested TTS characters exceed the selected profile server limit.'
}
$env:TOON_APP_ROOT = $toonRoot
$env:TOON_DATA_ROOT = $DataRoot
$env:TOON_IMPORT_ROOTS = $ImportRoots -join [IO.Path]::PathSeparator
$env:TOON_MAX_TTS_CHARACTERS = [string]$TtsLimit
$env:TOON_ALLOW_VOICE_CLONING = '0'
$env:TOON_MAX_VOICE_CLONES = '0'
$env:TOON_USE_USER_SECRETS = '1'
function Invoke-ToonCli([string[]]$Arguments) {
    $result = & dotnet $cli @Arguments
    if ($LASTEXITCODE) {
        throw "Toon CLI failed: $($Arguments[0])"
    }
    return ($result | Out-String | ConvertFrom-Json)
}
$doctor = Invoke-ToonCli -Arguments @('doctor')
if (!$doctor.renderer -or !$doctor.font -or @($doctor.dependencies.PSObject.Properties | Where-Object Value -eq 'unavailable').Count) {
    throw 'Dependencies are incomplete. Run ./scripts/studio.ps1 -Setup.'
}
if (!$doctor.worker_alive) {
    New-Item -ItemType Directory -Path (Join-Path $DataRoot 'studio') -Force | Out-Null
    $log = Join-Path $DataRoot ('studio/worker-' + [Guid]::NewGuid().ToString('N'))
    $launch = @{FilePath = (Get-Command dotnet).Source; ArgumentList = @('"' + $worker + '"'); WorkingDirectory = $toonRoot; RedirectStandardOutput = "$log.out.log"; RedirectStandardError = "$log.err.log"; PassThru = $true}
    if ($IsWindows) {
        $launch.WindowStyle = 'Hidden'
    }
    $startedWorker = Start-Process @launch
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        Start-Sleep -Milliseconds 500
        $doctor = Invoke-ToonCli -Arguments @('doctor')
        if ($doctor.worker_alive) {
            break
        }

        if ($startedWorker.HasExited) {
            throw "Worker exited. Inspect $log.err.log"
        }
    }
}
if (!$doctor.worker_alive) {
    throw 'Worker did not become ready.'
}
$workerConfig = $doctor.worker_configuration
if (!$workerConfig -or $workerConfig.worker_assembly_sha256 -ne $doctor.worker_assembly_sha256 -or $workerConfig.tts_character_limit -ne $TtsLimit -or ($workerConfig.import_roots -join ';') -ne ($ImportRoots -join ';')) {
    throw 'An existing worker has different or legacy settings. Finish its jobs, stop that worker and run this command again. It was not interrupted.'
}
@{data_root = $DataRoot; import_roots = $ImportRoots; tts_character_limit = $TtsLimit} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $profilePath -Encoding utf8
$name = "toon-$Profile"
if (!$NoConnectCodex) {
    & codex mcp add $name --env "TOON_APP_ROOT=$toonRoot" --env "TOON_DATA_ROOT=$DataRoot" --env "TOON_IMPORT_ROOTS=$env:TOON_IMPORT_ROOTS" --env "TOON_MAX_TTS_CHARACTERS=$TtsLimit" --env 'TOON_ALLOW_VOICE_CLONING=0' --env 'TOON_MAX_VOICE_CLONES=0' --env 'TOON_USE_USER_SECRETS=1' -- dotnet $mcp
    if ($LASTEXITCODE) {
        throw 'Worker is running, but Codex registration failed.'
    }
}
Write-Host "Studio ready: $Profile | MCP: $name | data: $DataRoot | worker PID: $($workerConfig.pid)"
if ($Brief) {
    if (!$EpisodeKey) {
        throw 'Provide -EpisodeKey to prevent duplicate creation on retry.'
    }
    $episode = Invoke-ToonCli -Arguments @('episode', [IO.Path]::GetFullPath($Brief), $EpisodeKey)
    $ProjectId = $episode.project_id
    $Revision = $episode.revision
    [ordered]@{
        project_id = $ProjectId
        revision = $Revision
        render_ready = $episode.readiness.render_ready
        new_tts_characters = $episode.readiness.estimated_new_tts_characters
        errors = $episode.readiness.errors
        warnings = $episode.readiness.warnings
    } | ConvertTo-Json -Depth 8 | Write-Output
}
if ($Preview) {
    if (!$ProjectId) {
        throw '-Preview requires -ProjectId or -Brief.'
    }
    $job = Invoke-ToonCli -Arguments @('prepare', $ProjectId, [string]$Revision, "studio-$ProjectId-r$Revision-tts$TtsCharacters", [string]$TtsCharacters)
    $JobId = $job.job_id
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while ($job.state -in @('queued', 'running', 'cancelling')) {
        if ([DateTime]::UtcNow -gt $deadline) {
            throw "Waiting timed out; worker continues. Resume with -JobId $JobId -Open."
        }
        Start-Sleep -Seconds 1
        $job = Invoke-ToonCli -Arguments @('status', $JobId)
    }
    if ($job.state -ne 'succeeded') {
        throw ($job.error | ConvertTo-Json -Compress)
    }
    Write-Host "Preview prepared: $JobId"
}
if ($Open) {
    if (!$JobId) {
        throw '-Open requires -JobId or -Preview.'
    }
    $previewInfo = Invoke-ToonCli -Arguments @('studio', $JobId)
    Write-Host "Remotion Studio: $($previewInfo.url)"
    if ($IsWindows) {
        # ShellExecute(URL) can block the caller on this Windows installation.
        Start-Process -FilePath explorer.exe -ArgumentList @($previewInfo.url) -WindowStyle Hidden
    } else {
        Start-Process -FilePath xdg-open -ArgumentList @($previewInfo.url)
    }
}
if (!$NoConnectCodex) {
    Write-Host "Open a new Codex session and use $name. No paid request is made by startup."
}
