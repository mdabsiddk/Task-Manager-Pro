<#
.SYNOPSIS
    Builds the GitHub release assets into .\dist :
      TaskManagerPro-Setup.exe   single-file installer (one UAC prompt)
      TaskManagerPro.zip         package: TaskManagerPro.exe + install.bat + uninstall.bat
      SHA256SUMS.txt             checksums (install.ps1 verifies the download with it)
      RELEASE_NOTES.md           text for the GitHub release page

.EXAMPLE
    .\tools\make-release.ps1                # version from src\AssemblyInfo.cs
    .\tools\make-release.ps1 -Version v2.0.0

    Used by .github\workflows\release.yml; works the same on any Windows PC (no SDK needed).
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler (csc.exe) not found.' }

if (-not $Version) {
    $info = Get-Content -Raw (Join-Path $root 'src\AssemblyInfo.cs')
    $Version = [regex]::Match($info, 'AssemblyFileVersion\("(\d+\.\d+\.\d+)').Groups[1].Value
}
$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 2.0.0 (got '$Version')." }
Write-Host "== Task Manager Pro ${Version}: release build"

# ---- 1. The program ----
& cmd.exe /c "`"$root\build.bat`" /quiet"
if ($LASTEXITCODE -ne 0) { throw 'build.bat failed.' }

# ---- 2. Package folder ----
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
$pkg = Join-Path $dist 'TaskManagerPro'
New-Item -ItemType Directory -Path $pkg | Out-Null
Copy-Item (Join-Path $root 'build\TaskManagerPro.exe'), (Join-Path $root 'build\TaskManagerPro.exe.config') $pkg
foreach ($bat in 'install.bat', 'uninstall.bat') {
    # Batch files need CRLF line endings, whatever git did on checkout.
    $text = [IO.File]::ReadAllText((Join-Path $root $bat)) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $pkg $bat), $text, (New-Object Text.UTF8Encoding $false))
}
$readme = @"
Task Manager Pro $Version
=========================

Install:    double-click install.bat  (answer Yes to the Windows prompt)
Uninstall:  Settings > Apps > Installed apps > Task Manager Pro > Uninstall
Emergency:  Start menu > "Task Manager Pro - Restore all apps"

https://github.com/mdabsiddk/Task-Manager-Pro
"@
[IO.File]::WriteAllText((Join-Path $pkg 'README.txt'), ($readme -replace "`r?`n", "`r`n"))

# ---- 3. ZIP ----
Compress-Archive -Path $pkg -DestinationPath (Join-Path $dist 'TaskManagerPro.zip')

# ---- 4. Single-file setup (package embedded as resources "pkg/<file>") ----
$versionFile = Join-Path $dist 'SetupVersion.cs'
@"
using System.Reflection;
[assembly: AssemblyTitle("Task Manager Pro Setup")]
[assembly: AssemblyProduct("Task Manager Pro")]
[assembly: AssemblyDescription("Installs Task Manager Pro")]
[assembly: AssemblyCopyright("Copyright (c) 2026")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
"@ | Set-Content -Path $versionFile -Encoding UTF8

$resources = foreach ($f in 'TaskManagerPro.exe', 'TaskManagerPro.exe.config', 'install.bat', 'uninstall.bat') {
    "-resource:$(Join-Path $pkg $f),pkg/$f"
}
& $csc -nologo -target:exe -optimize+ -platform:anycpu `
    "-out:$(Join-Path $dist 'TaskManagerPro-Setup.exe')" `
    "-win32manifest:$(Join-Path $root 'setup\setup.manifest')" `
    "-win32icon:$(Join-Path $root 'assets\app.ico')" `
    $resources (Join-Path $root 'setup\Setup.cs') $versionFile | Where-Object { $_ -notmatch 'C# 5|newer versions|no longer the latest|fwlink|^\s*$' }
if ($LASTEXITCODE -ne 0) { throw 'Building TaskManagerPro-Setup.exe failed.' }
Remove-Item $versionFile, $pkg -Recurse -Force

# ---- 5. Checksums ----
$sums = foreach ($f in 'TaskManagerPro-Setup.exe', 'TaskManagerPro.zip') {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 (Join-Path $dist $f)).Hash.ToLowerInvariant(), $f
}
[IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), (($sums -join "`n") + "`n"))

# ---- 6. Release notes ----
$repo = 'mdabsiddk/Task-Manager-Pro'
@"
## Task Manager Pro $Version

A Windows 11 style task manager that can limit how much **CPU, memory, disk and network** each app may use. Limits stay active after the app is closed.

### Install

**Option 1: one command** (PowerShell):
``````powershell
irm https://raw.githubusercontent.com/$repo/main/install.ps1 | iex
``````

**Option 2: download:** [TaskManagerPro-Setup.exe](https://github.com/$repo/releases/latest/download/TaskManagerPro-Setup.exe). Double-click it and answer **Yes**.

**Option 3: ZIP:** [TaskManagerPro.zip](https://github.com/$repo/releases/latest/download/TaskManagerPro.zip). Extract it and double-click ``install.bat``.

> Windows SmartScreen may warn because the file is new and not code-signed: click **More info → Run anyway**.
> Checksums are in ``SHA256SUMS.txt``.

### Uninstall
Use **Settings → Apps → Installed apps → Task Manager Pro → Uninstall**, or run:
``````powershell
irm https://raw.githubusercontent.com/$repo/main/uninstall.ps1 | iex
``````

Requirements: Windows 10 / 11 (64-bit recommended). Nothing else to install.
"@ | Set-Content -Path (Join-Path $dist 'RELEASE_NOTES.md') -Encoding UTF8

Write-Host "== Done:"
Get-ChildItem $dist | ForEach-Object { '   {0,-28} {1,10:N0} bytes' -f $_.Name, $_.Length }
