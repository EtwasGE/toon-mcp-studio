$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root 'examples/offline-dialogue'
New-Item -ItemType Directory -Force $dir | Out-Null
$voice = New-Object -ComObject SAPI.SpVoice
$ru = @($voice.GetVoices() | Where-Object { $_.GetDescription() -match 'Irina|Russian' })
if ($ru.Count -eq 0) { throw 'No local Russian SAPI voice; provide recorded WAV fixtures instead.' }
$voice.Voice = $ru[0]
$lines = @(
    @{id='problem';text='Опять весь вечер проверял сайты.';speaker='buyer'},
    @{id='idea';text='Давай оставим время для жизни.';speaker='advisor'},
    @{id='phone';text='Смотри: это учебный экран.';speaker='advisor'},
    @{id='reaction';text='Теперь всё видно на одном экране.';speaker='buyer'},
    @{id='home';text='А дома можно спокойно выпить чай.';speaker='buyer'},
    @{id='answer';text='И вернуться к своим планам.';speaker='advisor'}
)
foreach ($line in $lines) {
    $stream = New-Object -ComObject SAPI.SpFileStream
    $stream.Format.Type = 22
    $stream.Open((Join-Path $dir ($line.id + '.wav')), 3, $false)
    $voice.AudioOutputStream = $stream
    $voice.Rate = if ($line.speaker -eq 'buyer') { -2 } else { 0 }
    $pitch = if ($line.speaker -eq 'buyer') { -3 } else { 2 }
    [void]$voice.Speak("<pitch absmiddle='$pitch'>$($line.text)</pitch>", 8)
    $stream.Close()
}
$lines | ConvertTo-Json | Set-Content (Join-Path $dir 'lines.json') -Encoding utf8
@{source='Local Windows SAPI Microsoft Irina';kind='synthetic Russian speech fixture';human_recording=$false;word_alignment=$false;notes='Original test text. Same installed voice, two explicit pitch/rate presets. Not two ElevenLabs voices. Local QA only; do not claim redistribution rights for installed voice software.'} | ConvertTo-Json | Set-Content (Join-Path $dir 'provenance.json') -Encoding utf8
