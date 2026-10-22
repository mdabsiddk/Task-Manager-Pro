<#
.SYNOPSIS
    Sets the version of Task Manager Pro in its one place, the VERSION file, and adds a heading for
    it to CHANGELOG.md.

.DESCRIPTION
    Everything else follows VERSION by itself:
      program file, window, About, tray, updater   build.bat stamps it into TaskManagerPro.exe
      Installed apps (Settings)                     install.bat reads it from TaskManagerPro.exe
      Setup.exe, zip, release notes                 tools\make-release.ps1 reads VERSION
      GitHub Release and tag                        .github\workflows\release.yml, when VERSION
                                                    changes on main
      README badges                                 show the newest release

.EXAMPLE
    .\tools\set-version.ps1 2.3.0
#>
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 2.3.0 (got '$Version')." }

$old = (Get-Content -Raw (Join-Path $root 'VERSION')).Trim()
[IO.File]::WriteAllText((Join-Path $root 'VERSION'), "$Version`n")

$changelog = Join-Path $root 'CHANGELOG.md'
$text = [IO.File]::ReadAllText($changelog)
if ($text -notmatch "(?m)^## $([regex]::Escape($Version))\b") {
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $heading = "## $Version ($(Get-Date -Format 'yyyy-MM-dd'))$nl$nl- $nl$nl"
    $text = ([regex]'(?m)^## ').Replace($text, $heading.Replace('$', '$$') + '## ', 1)
    [IO.File]::WriteAllText($changelog, $text, (New-Object Text.UTF8Encoding $false))
}

Write-Host "Version: $old -> $Version"
Write-Host "Next: describe the changes under '## $Version' in CHANGELOG.md, then commit and push to main."
Write-Host "GitHub then builds and publishes release v$Version by itself."
