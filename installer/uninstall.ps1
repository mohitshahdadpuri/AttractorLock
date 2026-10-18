<#
  AttractorLock uninstaller (per user).

  Stops the tray app, removes the Startup and Start Menu shortcuts, the Settings > Apps
  entry and the installed files. Your password and logs are kept unless -RemoveData is given.

  Usage:  uninstall.cmd, or Settings > Apps > AttractorLock > Uninstall, or
          powershell -ExecutionPolicy Bypass -File uninstall.ps1 [-RemoveData]
#>
param(
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$AppName    = 'AttractorLock'
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$StartupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk"
$MenuLnk    = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$UninstKey  = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppName"

function Say($msg) { Write-Host "  $msg" }

# Windows can keep the exe locked for a moment after its process has exited (handle
# cleanup, antivirus scans), so file operations on it are retried for up to 10 seconds.
function Retry([scriptblock]$Action) {
    for ($i = 1; ; $i++) {
        try { & $Action; return }
        catch { if ($i -ge 20) { throw }; Start-Sleep -Milliseconds 500 }
    }
}

Write-Host "Uninstalling $AppName"

# ---- Stop the tray app (never a curtain that is up) ----
$running = Get-CimInstance Win32_Process -Filter "Name = '$AppName.exe'"
if ($running | Where-Object { $_.CommandLine -match '--curtain' }) {
    throw "The AttractorLock curtain is currently up. Close it with your password first, then uninstall."
}
foreach ($p in $running) {
    Say "Stopping running $AppName (pid $($p.ProcessId))"
    Stop-Process -Id $p.ProcessId -Force
    Wait-Process -Id $p.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}

# ---- Shortcuts and registry entry ----
foreach ($lnk in @($StartupLnk, $MenuLnk)) {
    if (Test-Path $lnk) { Remove-Item -LiteralPath $lnk -Force; Say "Removed $lnk" }
}
if (Test-Path $UninstKey) { Remove-Item -Path $UninstKey -Recurse -Force; Say "Removed Settings > Apps entry" }

# ---- Files ----
# This script may be running from inside the install folder, so it deletes the folder
# from a short-lived helper process once this script has exited.
if (Test-Path $InstallDir) {
    $exe = Join-Path $InstallDir "$AppName.exe"
    if (Test-Path $exe) { Retry { Remove-Item -LiteralPath $exe -Force } }
    $cmd = "Start-Sleep -Seconds 2; Remove-Item -LiteralPath '$InstallDir' -Recurse -Force -ErrorAction SilentlyContinue"
    Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile', '-WindowStyle', 'Hidden', '-Command', $cmd -WindowStyle Hidden
    Say "Removed $InstallDir"
}

# ---- Data (password, logs, browser cache) ----
$roaming = Join-Path $env:APPDATA $AppName
$local   = Join-Path $env:LOCALAPPDATA $AppName
if ($RemoveData) {
    foreach ($d in @($roaming, $local)) {
        if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force; Say "Removed $d" }
    }
} else {
    Say "Kept your password ($roaming). Use -RemoveData to delete it too."
}
Write-Host "Done."
