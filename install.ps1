# Installs dist\SnipClone for the current user: %LOCALAPPDATA%\Programs\SnipClone,
# plus Start Menu and Desktop shortcuts. Pass -Uninstall to remove it again.
param([switch]$Uninstall)

$ErrorActionPreference = "Stop"
$target = Join-Path $env:LOCALAPPDATA "Programs\SnipClone"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\SnipClone.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath("Desktop")) "SnipClone.lnk"

Get-Process SnipClone -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if ($Uninstall) {
    Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Write-Host "SnipClone uninstalled."
    exit 0
}

$source = Join-Path $PSScriptRoot "dist\SnipClone"
if (-not (Test-Path (Join-Path $source "SnipClone.exe"))) { throw "Build first: dist\SnipClone\SnipClone.exe not found." }

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $source $target -Recurse
$exe = Join-Path $target "SnipClone.exe"

$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in @($startMenu, $desktop)) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $exe
    $s.WorkingDirectory = $target
    $s.IconLocation = "$exe,0"
    $s.Description = "Clean-room screen snipping tool"
    $s.Save()
}
Write-Host "Installed to $target"
Write-Host "Shortcuts: $startMenu"
Write-Host "           $desktop"
