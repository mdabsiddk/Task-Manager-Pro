@echo off
:: ============================================================
::  Task Manager Pro - Installer
::  Uses the pre-built program from a release package (or
::  builds it from source), installs to Program Files, creates
::  shortcuts and an "Installed apps" entry, then starts it.
::  Upgrades Smart Task Manager (the previous name) in place.
::  Just double-click: it asks for administrator permission.
:: ============================================================
setlocal EnableExtensions
title Task Manager Pro - Installer
color 0A

:: ---- Self-elevate (UAC) ----
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
cd /d "%~dp0"

set "APP_NAME=Task Manager Pro"
set "EXE_NAME=TaskManagerPro.exe"
set "INSTALL_DIR=%ProgramFiles%\TaskManagerPro"
set "DATA_DIR=%ProgramData%\TaskManagerPro"
set "UNINSTALL_KEY=HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskManagerPro"
set "DESKTOP_LNK=%PUBLIC%\Desktop\%APP_NAME%.lnk"
set "STARTMENU_LNK=%ProgramData%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%.lnk"
set "RESTORE_LNK=%ProgramData%\Microsoft\Windows\Start Menu\Programs\%APP_NAME% - Restore all apps.lnk"
set "NGEN=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
if not exist "%NGEN%" set "NGEN=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\ngen.exe"

echo ========================================================
echo   Installing %APP_NAME%
echo ========================================================
echo.

:: ---- 1. Program file: the pre-built one from a release package, or build from source ----
set "SRC_DIR=%~dp0build\"
if exist "%~dp0%EXE_NAME%" (
    set "SRC_DIR=%~dp0"
    echo [*] Using the included pre-built %EXE_NAME%.
    goto :have_program
:: Version for "Installed apps", read from the program file itself (no number to update by hand).
set "APP_VERSION=2.0"
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v = (Get-Item -LiteralPath '%SRC_DIR%%EXE_NAME%').VersionInfo; '{0}.{1}.{2}' -f $v.FileMajorPart, $v.FileMinorPart, $v.FileBuildPart"`) do set "APP_VERSION=%%v"
echo [*] Version %APP_VERSION%
)
call "%~dp0build.bat" /quiet
if errorlevel 1 goto :fail
:have_program

:: ---- 2. Stop a running copy (limits stay on the apps; nothing is lost) ----
if exist "%INSTALL_DIR%\%EXE_NAME%" (
    echo [*] Stopping the running copy...
    start "" /wait "%INSTALL_DIR%\%EXE_NAME%" --exit
)
taskkill /IM %EXE_NAME% /F >nul 2>&1

:: ---- 3. Upgrade from Smart Task Manager (previous name) ----
call :remove_smart_task_manager

:: ---- 4. Copy files ----
echo [*] Copying files to %INSTALL_DIR%
if not exist "%INSTALL_DIR%" mkdir "%INSTALL_DIR%"
:: Windows may need a moment to release the old program file after it closed: retry up to ~10 s.
set /a COPY_TRIES=0
:copy_program
copy /Y "%SRC_DIR%%EXE_NAME%" "%INSTALL_DIR%\" >nul 2>&1 && goto :program_copied
set /a COPY_TRIES+=1
if %COPY_TRIES% geq 10 (
    echo [X] Could not replace %INSTALL_DIR%\%EXE_NAME% ^(still in use^). Close Task Manager Pro and try again.
    goto :fail
)
ping -n 2 127.0.0.1 >nul
goto :copy_program
:program_copied
copy /Y "%SRC_DIR%%EXE_NAME%.config" "%INSTALL_DIR%\" >nul || goto :fail
copy /Y "%~dp0uninstall.bat" "%INSTALL_DIR%\" >nul || goto :fail

:: ---- 5. Pre-compile to native code: faster start, less memory for the background engine ----
if exist "%NGEN%" (
    echo [*] Optimizing for this PC ^(native image^)...
    "%NGEN%" install "%INSTALL_DIR%\%EXE_NAME%" /nologo /silent >nul 2>&1
)

:: ---- 6. Shortcuts ----
echo [*] Creating Desktop and Start menu shortcuts...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$w = New-Object -ComObject WScript.Shell;" ^
  "foreach ($p in @('%DESKTOP_LNK%', '%STARTMENU_LNK%')) {" ^
  "  $s = $w.CreateShortcut($p); $s.TargetPath = '%INSTALL_DIR%\%EXE_NAME%';" ^
  "  $s.WorkingDirectory = '%INSTALL_DIR%'; $s.IconLocation = '%INSTALL_DIR%\%EXE_NAME%,0';" ^
  "  $s.Description = 'Task manager with per-app CPU, memory, disk and network limits'; $s.Save() }" ^
  "$r = $w.CreateShortcut('%RESTORE_LNK%'); $r.TargetPath = '%INSTALL_DIR%\%EXE_NAME%'; $r.Arguments = '--restore-all';" ^
  "$r.WorkingDirectory = '%INSTALL_DIR%'; $r.IconLocation = '%INSTALL_DIR%\%EXE_NAME%,0';" ^
  "$r.Description = 'Emergency: put every app back to normal and pause all limits'; $r.Save()"

:: ---- 7. Installed apps entry ----
echo [*] Registering in Installed apps...
reg add "%UNINSTALL_KEY%" /v DisplayName /d "%APP_NAME%" /f >nul
reg add "%UNINSTALL_KEY%" /v DisplayVersion /d "%APP_VERSION%" /f >nul
reg add "%UNINSTALL_KEY%" /v Publisher /d "Task Manager Pro" /f >nul
reg add "%UNINSTALL_KEY%" /v DisplayIcon /d "%INSTALL_DIR%\%EXE_NAME%" /f >nul
reg add "%UNINSTALL_KEY%" /v InstallLocation /d "%INSTALL_DIR%" /f >nul
reg add "%UNINSTALL_KEY%" /v UninstallString /d "\"%INSTALL_DIR%\uninstall.bat\"" /f >nul
reg add "%UNINSTALL_KEY%" /v NoModify /t REG_DWORD /d 1 /f >nul
reg add "%UNINSTALL_KEY%" /v NoRepair /t REG_DWORD /d 1 /f >nul

echo.
echo [SUCCESS] %APP_NAME% has been installed.
echo   - Open it from the Desktop or Start menu: "%APP_NAME%"
echo   - Set limits in the Max columns, then simply close it: the limits stay active.
echo   - Optional: Options ^(...^) ^> "Run in background" keeps exact limits for apps you open later.
echo   - If limits ever make an app unusable: Start menu ^> "%APP_NAME% - Restore all apps".
echo   - Limits and settings: %DATA_DIR% ^(administrators only^)
echo.
echo Starting %APP_NAME%...
:: Started through the shell so it doesn't inherit this console's handles.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%INSTALL_DIR%\%EXE_NAME%'"
pause
exit /b 0

:fail
color 0C
echo.
echo [X] Installation failed. See the messages above.
pause
exit /b 1

:: ------------------------------------------------------------
:remove_smart_task_manager
set "OLD_DIR=%ProgramFiles%\SmartTaskManager"
if not exist "%OLD_DIR%" if not exist "%ProgramData%\SmartTaskManager" exit /b 0
echo [*] Upgrading from Smart Task Manager...
if not exist "%DATA_DIR%" mkdir "%DATA_DIR%"
if not exist "%DATA_DIR%\limits.cfg" if exist "%ProgramData%\SmartTaskManager\limits.cfg" copy /Y "%ProgramData%\SmartTaskManager\limits.cfg" "%DATA_DIR%\limits.cfg" >nul
if not exist "%DATA_DIR%\limits.cfg" if exist "%OLD_DIR%\limits.cfg" copy /Y "%OLD_DIR%\limits.cfg" "%DATA_DIR%\limits.cfg" >nul
:: The previous version understood --exit (and restored the apps); its first version did not.
if exist "%OLD_DIR%\SmartTaskMgr.exe" if not exist "%OLD_DIR%\SmartTaskMgr.cs" start "" /wait "%OLD_DIR%\SmartTaskMgr.exe" --exit
taskkill /IM SmartTaskMgr.exe /F >nul 2>&1
schtasks /Delete /TN "SmartTaskManager" /F >nul 2>&1
reg delete "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" /v SmartTaskManager /f >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SmartTaskManager" /f >nul 2>&1
del /q "%PUBLIC%\Desktop\Smart Task Manager.lnk" >nul 2>&1
del /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\Smart Task Manager.lnk" >nul 2>&1
if exist "%OLD_DIR%" rmdir /s /q "%OLD_DIR%"
exit /b 0
