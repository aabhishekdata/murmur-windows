# Generates the spoken clips the E2E suite expects, with Windows' built-in text-to-speech:
# 16 kHz mono PCM16 WAV files plus expected.tsv. Nothing to download.
#
#   .\make-clips.ps1 -OutputDirectory $env:TEMP\murmur-clips
#   $env:MURMUR_E2E_AUDIO = "$env:TEMP\murmur-clips"
param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
Add-Type -AssemblyName System.Speech

$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

$clips = [ordered]@{
    'hello'         = 'Hello world, this is a dictation test.'
    'sentence'      = 'The quick brown fox jumps over the lazy dog.'
    'trigger'       = 'review prompt'
    'trigger-enter' = 'review prompt press enter'
    'filler'        = 'So um I think we should, uh, ship it on Friday.'
    # Over 30 seconds, so the cloud path sends chunking_strategy for real.
    'long'          = ('This is a longer dictation that keeps going for a while so that the audio is longer than thirty seconds. ' * 5)
}

foreach ($name in $clips.Keys) {
    $voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $voice.SetOutputToWaveFile((Join-Path $OutputDirectory "$name.wav"), $format)
        $voice.Speak($clips[$name])
    }
    finally {
        $voice.Dispose()
    }
}

$clips.GetEnumerator() | ForEach-Object { "$($_.Key)`t$($_.Value)" } |
    Set-Content (Join-Path $OutputDirectory 'expected.tsv') -Encoding utf8

Write-Host "Wrote $($clips.Count) clips to $OutputDirectory"
