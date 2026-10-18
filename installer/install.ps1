<#
  AttractorLock installer (per user, no admin rights needed).

  - Copies AttractorLock.exe to %LOCALAPPDATA%\Programs\AttractorLock
  - Starts it with Windows (shortcut in the Startup folder)
  - Adds a Start Menu shortcut and an entry in Settings > Apps (with uninstall)
  - Launches it

  Your password (%APPDATA%\AttractorLock\cfg.dat) is never touched, so reinstalling or
  upgrading keeps it.

  Usage:  install.cmd                (double-click), or
          powershell -ExecutionPolicy Bypass -File install.ps1 [-Source <path to exe>] [-NoLaunch]
#>
param(
    [string]$Source,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$AppName    = 'AttractorLock'
$InstallDir = Join-Path $env:LOCALAPPDATA "Programs\$AppName"
$Exe        = Join-Path $InstallDir "$AppName.exe"
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

# ---- 1. Find the exe to install ----
if (-not $Source) {
    $candidates = @(
        (Join-Path $PSScriptRoot "$AppName.exe"),
        (Join-Path $PSScriptRoot "..\publish\$AppName.exe")
    )
    $Source = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Source -or -not (Test-Path $Source)) {
    throw "Can't find $AppName.exe. Put it next to install.ps1 or pass -Source <path>."
}
$Source = (Resolve-Path $Source).Path
Write-Host "Installing $AppName from $Source"

# ---- 2. Stop the running tray app (never a curtain that is up) ----
$running = Get-CimInstance Win32_Process -Filter "Name = '$AppName.exe'"
if ($running | Where-Object { $_.CommandLine -match '--curtain' }) {
    throw "The AttractorLock curtain is currently up. Close it with your password first, then run the installer again."
}
foreach ($p in $running) {
    Say "Stopping running $AppName (pid $($p.ProcessId))"
    Stop-Process -Id $p.ProcessId -Force
    Wait-Process -Id $p.ProcessId -Timeout 10 -ErrorAction SilentlyContinue
}

# ---- 3. Copy files ----
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ($Source -ne $Exe) {
    Retry { Copy-Item -LiteralPath $Source -Destination $Exe -Force }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'uninstall.ps1') -Destination (Join-Path $InstallDir 'uninstall.ps1') -Force
Say "Copied to $InstallDir"

# ---- 4. Shortcuts ----
$shell = New-Object -ComObject WScript.Shell
foreach ($lnkPath in @($StartupLnk, $MenuLnk)) {
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath       = $Exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.IconLocation     = "$Exe,0"
    $lnk.Description      = 'AttractorLock privacy curtain (Ctrl+Break)'
    $lnk.Save()
}
Say "Starts with Windows: $StartupLnk"
Say "Start Menu shortcut: $MenuLnk"

# ---- 5. Settings > Apps entry ----
$version = (Get-Item $Exe).VersionInfo.ProductVersion
if (-not $version) { $version = '1.0.0' }
$version = $version.Split('+')[0]   # drop the build hash suffix
New-Item -Path $UninstKey -Force | Out-Null
$uninstallCmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
$props = @{
    DisplayName     = $AppName
    DisplayVersion  = $version
    Publisher       = $env:USERNAME
    InstallLocation = $InstallDir
    DisplayIcon     = "$Exe,0"
    EstimatedSize   = [int][math]::Ceiling((Get-Item $Exe).Length / 1KB)   # in KB; shown as the size in Settings
    UninstallString = $uninstallCmd
    NoModify        = 1
    NoRepair        = 1
}
foreach ($k in $props.Keys) {
    $type = if ($props[$k] -is [int]) { 'DWord' } else { 'String' }
    New-ItemProperty -Path $UninstKey -Name $k -Value $props[$k] -PropertyType $type -Force | Out-Null
}
Say "Registered in Settings > Apps"

# ---- 6. Launch ----
if (-not $NoLaunch) {
    Start-Process -FilePath $Exe -WorkingDirectory $InstallDir
    Say "Started. Look for the AttractorLock icon (a butterfly) in the system tray."
}

$pw = Join-Path $env:APPDATA "$AppName\cfg.dat"
if (Test-Path $pw) { Say "Your existing password is kept." }
else { Say "First run: you'll be asked to set a password." }
Write-Host "Done. Press Ctrl+Break (or double-click the tray icon) to cover the screen."
