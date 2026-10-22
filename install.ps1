<#
.SYNOPSIS
    Installs or updates Task Manager Pro straight from GitHub.

.DESCRIPTION
    Run in PowerShell:

        irm https://raw.githubusercontent.com/mdabsiddk/Task-Manager-Pro/main/install.ps1 | iex

    1. Asks for administrator permission (one Windows prompt) if needed.
    2. Downloads TaskManagerPro.zip from the latest GitHub release and checks its SHA-256 checksum.
       If there is no release yet, downloads the latest source code instead and builds it
       (the C# compiler that ships with Windows is enough).
    3. Runs install.bat from the package, then removes the downloaded files.
#>
[CmdletBinding()]
param(
    # Install from a local TaskManagerPro.zip instead of downloading (for testing).
    [string]$Package,
    # Set when this script re-opened itself as administrator: keep that window open at the end.
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # much faster downloads in Windows PowerShell 5.1
$Repo = 'mdabsiddk/Task-Manager-Pro'
$ScriptUrl = "https://raw.githubusercontent.com/$Repo/main/install.ps1"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-Step([string]$Text) { Write-Host "[*] $Text" -ForegroundColor Cyan }

if (-not (Test-Admin)) {
    Write-Host 'Task Manager Pro needs administrator permission to install. Please answer "Yes" to the Windows prompt.' -ForegroundColor Yellow
    if ($PSCommandPath) {
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-KeepOpen')
        if ($Package) { $arguments += @('-Package', "`"$Package`"") }
    } else {
        $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', "`$env:TMPRO_KEEP_OPEN='1'; irm '$ScriptUrl' | iex")
    }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments
    } catch {
        Write-Host 'Installation cancelled (administrator permission was not given).' -ForegroundColor Red
    }
    return
}

$work = Join-Path $env:TEMP ('TaskManagerPro-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$failed = $false
try {
    $zip = Join-Path $work 'TaskManagerPro.zip'
    $fromSource = $false

    if ($Package) {
        Write-Step "Using local package $Package"
        Copy-Item -LiteralPath $Package -Destination $zip
    } else {
        $headers = @{ 'User-Agent' = 'TaskManagerPro-Installer'; 'Accept' = 'application/vnd.github+json' }
        $release = $null
        try { $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers } catch { }
        $asset = if ($release) { $release.assets | Where-Object { $_.name -eq 'TaskManagerPro.zip' } | Select-Object -First 1 }

        if ($asset) {
            Write-Step "Downloading Task Manager Pro $($release.tag_name)..."
            Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing -Headers $headers

            $sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
            if ($sumsAsset) {
                Write-Step 'Verifying the download (SHA-256)...'
                $sums = (Invoke-WebRequest -Uri $sumsAsset.browser_download_url -UseBasicParsing -Headers $headers).Content
                if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
                $line = ($sums -split "`n") | Where-Object { $_ -match 'TaskManagerPro\.zip\s*$' } | Select-Object -First 1
                $expected = if ($line) { ($line -split '\s+')[0].Trim().ToLowerInvariant() }
                $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
                if (-not $expected -or $expected -ne $actual) { throw 'The download is damaged or was changed (checksum mismatch). Nothing was installed.' }
            }
        } else {
            Write-Step 'No release found yet: downloading the latest source code...'
            Invoke-WebRequest -Uri "https://github.com/$Repo/archive/refs/heads/main.zip" -OutFile $zip -UseBasicParsing -Headers $headers
            $fromSource = $true
        }
    }

    Write-Step 'Extracting...'
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $work 'pkg') -Force
    $installer = Get-ChildItem -Path (Join-Path $work 'pkg') -Recurse -Filter 'install.bat' | Select-Object -First 1
    if (-not $installer) { throw 'install.bat was not found in the download.' }

    if ($fromSource) {
        # Make sure batch files have Windows line endings before running them.
        Get-ChildItem -Path $installer.DirectoryName -Filter '*.bat' | ForEach-Object {
            $text = [IO.File]::ReadAllText($_.FullName) -replace "`r?`n", "`r`n"
            [IO.File]::WriteAllText($_.FullName, $text, (New-Object Text.UTF8Encoding $false))
        }
    }

    Write-Step 'Installing...'
    & cmd.exe /c "`"$($installer.FullName)`" < nul"
    if ($LASTEXITCODE -ne 0) { throw "install.bat reported an error (exit code $LASTEXITCODE)." }

    Write-Host ''
    Write-Host 'Task Manager Pro is installed. Open it from the Start menu or the Desktop.' -ForegroundColor Green
} catch {
    $failed = $true
    Write-Host ''
    Write-Host "[X] $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($KeepOpen -or $env:TMPRO_KEEP_OPEN -eq '1') {
    # This window was opened just for the installation: keep it readable.
    Read-Host 'Press Enter to close'
}
if ($failed -and $PSCommandPath) { exit 1 }
