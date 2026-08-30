# Downloads the Parakeet v2 (English) speech model Murmur needs to transcribe.
# One-time download, ~661 MB. Run:  powershell -ExecutionPolicy Bypass -File .\get-model.ps1
$dir = "$env:LOCALAPPDATA\Murmur\models\parakeet-v2"
$base = "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/main"

New-Item -ItemType Directory -Force $dir | Out-Null

foreach ($f in "encoder.int8.onnx","decoder.int8.onnx","joiner.int8.onnx","tokens.txt") {
    Write-Host "Downloading $f ..."
    curl.exe -L --fail --progress-bar -o "$dir\$f" "$base/$f"
}

Get-ChildItem $dir | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
Write-Host "`nDone. Launch Murmur.App.exe - Settings > Model should show the resolved path."
