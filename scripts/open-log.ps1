$log = Join-Path $env:LOCALAPPDATA "LocalTypeAssist\logs\localtypeassist.log"
if (Test-Path $log) {
    notepad.exe $log
} else {
    Write-Host "Log file does not exist yet: $log"
}
