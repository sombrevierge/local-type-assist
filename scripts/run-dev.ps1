$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\LocalTypeAssist\LocalTypeAssist.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден dotnet. Установите .NET 10 SDK (x64), затем откройте новое окно PowerShell."
}

dotnet run --project $project -c Debug
