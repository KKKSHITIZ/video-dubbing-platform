# One-command dev/demo runner.
# Configures the API to run as a complete, self-contained demo using SQLite + in-process
# processing (Messaging:NoOpProcessInProcess=true), with Mock/local AI providers.
# Requires NO external services (no RabbitMQ, no Postgres, no Docker).
# Requires FFmpeg + FFprobe for real media processing (auto-detected below).
$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location (Split-Path -Parent $scriptDir)

function Get-ExePath([string]$name) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $base = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages\Gyan.FFmpeg*\ffmpeg-*\bin\$name.exe"
    $hit = Get-ChildItem -Path $base -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit) { return $hit.FullName }
    $base2 = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages\Gyan.FFmpeg*\*\bin\$name.exe"
    $hit2 = Get-ChildItem -Path $base2 -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($hit2) { return $hit2.FullName }
    return $null
}

$ffmpeg = Get-ExePath "ffmpeg"
$ffprobe = Get-ExePath "ffprobe"
if (-not $ffmpeg -or -not $ffprobe) {
    Write-Host "FFmpeg not found. Install it with:  winget install Gyan.FFmpeg" -ForegroundColor Yellow
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Database__Provider = "Sqlite"
$env:ConnectionStrings__Sqlite = "Data Source=videodubbing.db"
$env:Messaging__Provider = "None"
$env:Messaging__NoOpProcessInProcess = "true"
$env:Processing__StepDelayMs = "700"
$env:Storage__Provider = "Local"
$env:Storage__LocalRoot = "./data"
$env:Providers__SpeechToText__Primary = "Mock"
$env:Providers__Translation__Primary = "Mock"
$env:Providers__Voice__Primary = "LocalFfmpeg"
$env:Providers__Diarization__Primary = "FfmpegSilence"
$env:Security__EnableRateLimiting = "false"
if ($ffmpeg)  { $env:Media__FfmpegPath  = $ffmpeg }
if ($ffprobe) { $env:Media__FfprobePath = $ffprobe }

Write-Host "Starting Video Dubbing API (demo mode)" -ForegroundColor Cyan
Write-Host "  Dashboard:  http://localhost:5043" -ForegroundColor Green
Write-Host "  Swagger:    http://localhost:5043/swagger" -ForegroundColor Green
Write-Host "Built khalas hote hi browser me ye URL kholo: http://localhost:5043" -ForegroundColor Yellow

dotnet run --project src/VideoDubbing.Api