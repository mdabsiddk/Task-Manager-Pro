@echo off
:: ============================================================
::  Task Manager Pro - Uninstaller
::  Puts every limited app back to normal, removes the
::  persistent limits (registry PerfOptions + network QoS),
::  autostart, shortcuts and program files.
:: ============================================================
setlocal EnableExtensions
title Task Manager Pro - Uninstaller

:: ---- Self-elevate (UAC) ----
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator permission...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "APP_NAME=Task Manager Pro"
set "EXE_NAME=TaskManagerPro.exe"
set "INSTALL_DIR=%ProgramFiles%\TaskManagerPro"
set "DATA_DIR=%ProgramData%\TaskManagerPro"
set "NGEN=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\ngen.exe"
if not exist "%NGEN%" set "NGEN=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\ngen.exe"

echo ========================================================
echo   Uninstalling %APP_NAME%
echo ========================================================
echo.

echo [*] Restoring every limited app and removing persistent limits...
:: Close an open window first (it would keep applying limits), then undo everything.
taskkill /IM %EXE_NAME% /F >nul 2>&1
if exist "%INSTALL_DIR%\%EXE_NAME%" start "" /wait "%INSTALL_DIR%\%EXE_NAME%" --release-all

echo [*] Removing autostart...
schtasks /Delete /TN "TaskManagerPro" /F >nul 2>&1

echo [*] Removing network limits (QoS policies)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Get-NetQosPolicy -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'TaskManagerPro_*' -or $_.Name -like 'SmartLimit_*' } | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue"

echo [*] Removing shortcuts and the Installed apps entry...
if exist "%NGEN%" "%NGEN%" uninstall "%INSTALL_DIR%\%EXE_NAME%" /nologo /silent >nul 2>&1
del /q "%PUBLIC%\Desktop\%APP_NAME%.lnk" >nul 2>&1
del /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\%APP_NAME%.lnk" >nul 2>&1
del /q "%ProgramData%\Microsoft\Windows\Start Menu\Programs\%APP_NAME% - Restore all apps.lnk" >nul 2>&1
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskManagerPro" /f >nul 2>&1

if exist "%DATA_DIR%" (
    echo.
    choice /C YN /M "Also delete your saved limits and settings (%DATA_DIR%)"
    if not errorlevel 2 rmdir /s /q "%DATA_DIR%"
)

echo.
echo [SUCCESS] %APP_NAME% has been removed.
pause

:: Delete the program folder last. This script may live inside it, so leave the
:: folder first; "(goto)" ends the script before cmd tries to read the deleted file.
cd /d "%TEMP%"
(goto) 2>nul & rmdir /s /q "%INSTALL_DIR%"
