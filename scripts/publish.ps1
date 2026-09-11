# Build + test + publish the solution to ./publish
$ErrorActionPreference = "Stop"
Write-Host "Building solution..." -ForegroundColor Cyan
dotnet build VideoDubbingPlatform.sln -c Release

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test VideoDubbingPlatform.sln -c Release --no-build

Write-Host "Publishing (self-contained framework-dependent)..." -ForegroundColor Cyan
dotnet publish src/VideoDubbing.Api -c Release -o publish/api
dotnet publish src/VideoDubbing.Worker -c Release -o publish/worker

Write-Host "Done. Run:" -ForegroundColor Green
Write-Host "  .\publish\api\VideoDubbing.Api.exe"
Write-Host "  .\publish\worker\VideoDubbing.Worker.exe"
