$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$requirements = Join-Path $root "src\LocalTypeAssist\Resources\ml\requirements-ml.txt"
$dataRoot = Join-Path $env:LOCALAPPDATA "LocalTypeAssist"
$venv = Join-Path $dataRoot "ml-venv"
$venvPython = Join-Path $venv "Scripts\python.exe"

$python = $null
$prefix = @()
if (Get-Command py -ErrorAction SilentlyContinue) {
    $python = "py"
    $prefix = @("-3")
} elseif (Get-Command python -ErrorAction SilentlyContinue) {
    $python = "python"
} else {
    throw "Python 3 не найден. Установите Python 3, откройте новый PowerShell и запустите скрипт снова."
}

New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

if (-not (Test-Path $venvPython)) {
    Write-Host "Creating isolated ML environment in $venv ..."
    & $python @prefix -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw "Не удалось создать ML virtual environment." }
}

Write-Host "Installing local ML dependencies..."
& $venvPython -m pip install --upgrade pip
if ($LASTEXITCODE -ne 0) { throw "pip upgrade failed." }
& $venvPython -m pip install -r $requirements
if ($LASTEXITCODE -ne 0) { throw "ML dependency installation failed." }

Write-Host ""
Write-Host "ML dependencies are ready in %LOCALAPPDATA%\LocalTypeAssist\ml-venv." -ForegroundColor Green
Write-Host "Open Local Type Assist -> Библиотека обучения -> Переобучить ML."
