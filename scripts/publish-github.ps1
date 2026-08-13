param(
    [string]$RepositoryName = "local-type-assist",
    [string]$Description = "Local-first text autocomplete for Windows with personal learning and Russian morphology"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
Set-Location $ProjectRoot

Write-Host "Stopping Local Type Assist..."
Get-Process LocalTypeAssist -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700

Write-Host "Removing local build output and obsolete files..."
$pathsToRemove = @(
    ".vs", ".vscode", ".idea", "dist", "publish", "artifacts", "TestResults",
    "src\LocalTypeAssist\bin", "src\LocalTypeAssist\obj",
    "BEHAVIOR_TESTS_V6_6.md", "BEHAVIOR_TESTS_V6_7.md", "BEHAVIOR_TESTS_V6_8.md",
    "START_HERE.txt", "ARCHITECTURE.md",
    "V6_CHANGES.md", "V6_2_CHANGES.md", "V6_4_CHANGES.md", "V6_5_CHANGES.md",
    "V6_6_CHANGES.md", "V6_6_1_FIX.md", "V6_7_CHANGES.md",
    "V6_7_1_COMPILE_FIX.md", "V6_7_2_COMPILE_FIX.md", "V6_8_CHANGES.md",
    "docs\ui-reference-v6.png"
)
foreach ($relativePath in $pathsToRemove) {
    $path = Join-Path $ProjectRoot $relativePath
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

Get-ChildItem $ProjectRoot -Recurse -File -Include *.zip,*.7z,*.rar,*.log,*.tmp |
    Where-Object { $_.FullName -notlike "*\.git\*" } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host "Checking required tools..."
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "Git is not installed. Install it with: winget install --id Git.Git -e"
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is not installed. Install it with: winget install --id GitHub.cli -e"
}

Write-Host "Checking for accidentally tracked secrets or personal model data..."
$forbidden = Get-ChildItem $ProjectRoot -Recurse -File |
    Where-Object {
        $_.FullName -notlike "*\.git\*" -and
        ($_.Name -match '^(settings|profile|secrets)\.json$' -or $_.Name -match '\.(pfx|p12|key|pem)$')
    }
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Host "Unsafe file: $($_.FullName)" -ForegroundColor Red }
    throw "Remove the files listed above before publishing."
}

Write-Host "Building before publication..."
& "$ProjectRoot\scripts\build-release.ps1"
if ($LASTEXITCODE -ne 0) {
    throw "Build failed. Repository was not published."
}
Remove-Item "$ProjectRoot\dist" -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path "$ProjectRoot\.git")) {
    git init
}

git branch -M main

if (-not (git config user.name)) {
    git config user.name "Catherine Moon"
}
if (-not (git config user.email)) {
    git config user.email "125189715+sombrevierge@users.noreply.github.com"
}

git add .
$staged = git diff --cached --name-only
if (-not $staged) {
    Write-Host "There are no new files to commit."
} else {
    git commit -m "Update Local Type Assist"
}

gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "GitHub authentication is required. A browser may open."
    gh auth login --web --git-protocol https
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub authentication failed."
    }
}

$hasOrigin = (git remote) -contains "origin"
if ($hasOrigin) {
    Write-Host "Using existing origin remote."
    git push -u origin main
} else {
    gh repo create $RepositoryName `
        --public `
        --source . `
        --remote origin `
        --push `
        --description $Description
}

gh repo edit --visibility public --accept-visibility-change-consequences
if ($LASTEXITCODE -ne 0) {
    throw "Could not confirm public repository visibility."
}
gh repo edit --enable-issues=true --enable-wiki=false
$topics = @("windows", "wpf", "csharp", "autocomplete", "local-first", "russian-language")
foreach ($topic in $topics) {
    gh repo edit --add-topic $topic
}

Write-Host "Published successfully:" -ForegroundColor Green
gh repo view --web
