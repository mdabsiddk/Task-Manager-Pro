<#
.SYNOPSIS
    Uninstalls Task Manager Pro.

.DESCRIPTION
    Run in PowerShell:

        irm https://raw.githubusercontent.com/mdabsiddk/Task-Manager-Pro/main/uninstall.ps1 | iex

    Asks for administrator permission if needed, then runs the installed uninstall.bat. That
    puts every limited app back to normal, removes all limits, shortcuts and program files,
    and asks whether to keep your saved limits.
#>
$ErrorActionPreference = 'Stop'
$ScriptUrl = 'https://raw.githubusercontent.com/mdabsiddk/Task-Manager-Pro/main/uninstall.ps1'
$Uninstaller = Join-Path $env:ProgramFiles 'TaskManagerPro\uninstall.bat'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not (Test-Path -LiteralPath $Uninstaller)) {
    Write-Host 'Task Manager Pro is not installed on this PC.' -ForegroundColor Yellow
    return
}

if (-not $isAdmin) {
    Write-Host 'Uninstalling needs administrator permission. Please answer "Yes" to the Windows prompt.' -ForegroundColor Yellow
    $arguments = if ($PSCommandPath) {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    } else {
        @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "irm '$ScriptUrl' | iex")
    }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    } catch {
        Write-Host 'Uninstall cancelled (administrator permission was not given).' -ForegroundColor Red
    }
    return
}

# uninstall.bat asks whether to keep the saved limits, then deletes its own folder.
& cmd.exe /c "`"$Uninstaller`""
