$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\LocalTypeAssist\LocalTypeAssist.csproj"
$dist = Join-Path $root "dist"
$nuget = "https://api.nuget.org/v3/index.json"
$runtime = "win-x64"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet was not found. Add the .NET 10 SDK folder to PATH and run this script again."
}

if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host "Restoring packages for $runtime..."
dotnet restore $project -r $runtime --source $nuget
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host "Publishing Local Type Assist..."
dotnet publish $project `
    -c Release `
    -r $runtime `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $dist
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $dist "LocalTypeAssist.exe"
if (-not (Test-Path $exe)) {
    throw "Publish finished without creating $exe."
}

Write-Host ""
Write-Host "Build complete: $exe" -ForegroundColor Green
Write-Host "Run the EXE by double-clicking it. Keep all files in the dist folder together." -ForegroundColor Green
