$ErrorActionPreference = "Stop"
$data = Join-Path $env:LOCALAPPDATA "LocalTypeAssist"

if (-not (Test-Path $data)) {
    Write-Host "Локальных данных пока нет."
    exit 0
}

$answer = Read-Host "Удалить все профили и настройки Local Type Assist? Введите DELETE"
if ($answer -ne "DELETE") {
    Write-Host "Отменено."
    exit 0
}

Remove-Item $data -Recurse -Force
Write-Host "Все локальные данные удалены."
