<#
.SYNOPSIS
    Installs Session Limit for the current user.

.DESCRIPTION
    Copies the published executable to %LOCALAPPDATA%\Programs\SessionLimit, creates
    Start Menu and desktop shortcuts, and registers it to launch on login.

    Everything is per-user, so no administrator rights are required at any point.
    Launch-on-login uses the HKCU Run key rather than a scheduled task, which means it
    shows up in Task Manager's Startup tab and can be disabled there like any other app.

.PARAMETER Uninstall
    Removes the shortcuts, the Run entry and the installed files. Leaves settings and
    usage history in %APPDATA%\SessionLimit alone.

.PARAMETER NoStartup
    Installs without registering launch-on-login.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$NoStartup
)

$ErrorActionPreference = 'Stop'

$AppName    = 'Session Limit'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\SessionLimit'
$ExePath    = Join-Path $InstallDir 'SessionLimit.exe'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunValue   = 'SessionLimit'
$StartMenu  = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$AppName.lnk"
$Desktop    = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"

function Stop-Running {
    Get-Process SessionLimit -ErrorAction SilentlyContinue | ForEach-Object {
        Write-Host "  stopping running instance (pid $($_.Id))"
        $_ | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 600
}

function New-Shortcut([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($Path)
    $sc.TargetPath       = $ExePath
    $sc.WorkingDirectory = $InstallDir
    $sc.IconLocation     = "$ExePath,0"
    $sc.Description      = 'Live Claude usage overlay'
    $sc.Save()
}

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    Write-Host "Uninstalling $AppName..." -ForegroundColor Cyan
    Stop-Running

    foreach ($lnk in @($StartMenu, $Desktop)) {
        if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "  removed $lnk" }
    }
    if (Get-ItemProperty -Path $RunKey -Name $RunValue -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKey -Name $RunValue
        Write-Host '  removed launch-on-login entry'
    }
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force; Write-Host "  removed $InstallDir" }

    Write-Host 'Done. Settings and usage history kept in %APPDATA%\SessionLimit.' -ForegroundColor Green
    return
}

# ------------------------------------------------------------------ install
$source = Join-Path $PSScriptRoot 'publish\SessionLimit.exe'
if (-not (Test-Path $source)) {
    throw "Not found: $source`nPublish first:`n  dotnet publish SessionLimit/SessionLimit.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish"
}

Write-Host "Installing $AppName..." -ForegroundColor Cyan
Stop-Running

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $source $ExePath -Force
Write-Host "  installed to $InstallDir ($([math]::Round((Get-Item $ExePath).Length / 1MB, 1)) MB)"

New-Shortcut $StartMenu; Write-Host '  Start Menu shortcut created'
New-Shortcut $Desktop;   Write-Host '  desktop shortcut created'

if ($NoStartup) {
    Write-Host '  skipped launch-on-login (-NoStartup)'
} else {
    Set-ItemProperty -Path $RunKey -Name $RunValue -Value "`"$ExePath`"" -Type String
    Write-Host '  registered to launch on login'
}

Write-Host "`nDone. Starting $AppName..." -ForegroundColor Green
Start-Process $ExePath
