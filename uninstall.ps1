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
    # The installed uninstall.bat can be elevated directly: no script download involved.
    try {
        Start-Process -FilePath $Uninstaller -Verb RunAs
        Write-Host 'The uninstall continues in the new window.' -ForegroundColor Green
    } catch {
        Write-Host "Uninstall did not start: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host 'If you clicked "No", run the command again and answer "Yes".' -ForegroundColor Yellow
    }
    return
}

# uninstall.bat asks whether to keep the saved limits, then deletes its own folder.
& cmd.exe /c "`"$Uninstaller`""
