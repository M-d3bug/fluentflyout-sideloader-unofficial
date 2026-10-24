# Shows the setup window WITHOUT installing anything (no admin rights needed).
# Useful for eyeballing the styling. Close the window when done.
$exe = Get-ChildItem -LiteralPath (Split-Path -Parent $PSScriptRoot) -Filter 'FluentFlyout-Sideloader_*_x64_Setup.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) { throw 'Build the setup exe first: powershell -ExecutionPolicy Bypass -File .\build.ps1' }
& $exe.FullName /preview
exit 0
