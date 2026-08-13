$ErrorActionPreference = "Stop"
$data = Join-Path $env:LOCALAPPDATA "LocalTypeAssist"

if (-not (Test-Path $data)) {
    Write-Host "Локальных данных пока нет."
    exit 0
}

$answer = Read-Host "Удалить ВСЕ локальные данные Local Type Assist (профили, историю обучения, SQLite-базу, ML-модели, ML-окружение и настройки)? Введите DELETE"
if ($answer -ne "DELETE") {
    Write-Host "Отменено."
    exit 0
}

Remove-Item $data -Recurse -Force
Write-Host "Все локальные данные, история обучения и персональные модели удалены."
