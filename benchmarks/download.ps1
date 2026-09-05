$ErrorActionPreference = 'Stop'
$benchmarkDataDirectory = Join-Path $PSScriptRoot '../data/benchmark'
New-Item -ItemType Directory -Force -Path $benchmarkDataDirectory | Out-Null
$benchmarkDatasetPath = Join-Path $benchmarkDataDirectory 'longmemeval_s_cleaned.json'
$benchmarkDatasetHash = 'D6F21EA9D60A0D56F34A05B609C79C88A451D2AE03597821EA3D5A9678C3A442'
if (-not (Test-Path -LiteralPath $benchmarkDatasetPath)) {
    Invoke-WebRequest -Uri 'https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/main/longmemeval_s_cleaned.json' -OutFile $benchmarkDatasetPath
}
if ((Get-FileHash -LiteralPath $benchmarkDatasetPath -Algorithm SHA256).Hash -ne $benchmarkDatasetHash) {
    throw 'Dataset checksum differs from the pinned LongMemEval-S release.'
}
Write-Output 'Pinned LongMemEval-S dataset is ready.'
