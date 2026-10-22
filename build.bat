@echo off
:: ============================================================
::  Task Manager Pro - Build script
::  Compiles src\**\*.cs into build\TaskManagerPro.exe with the
::  C# compiler that ships with Windows (.NET Framework 4.x).
::  No Visual Studio or SDK needed.   Usage: build.bat [/quiet]
:: ============================================================
setlocal EnableExtensions
set "ROOT=%~dp0"
set "OUT_DIR=%ROOT%build"
set "OUT_EXE=%OUT_DIR%\TaskManagerPro.exe"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [X] .NET Framework 4.x C# compiler ^(csc.exe^) was not found.
    echo     Enable ".NET Framework 4.8" in "Turn Windows features on or off".
    goto :fail
)

if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"

echo [*] Compiling Task Manager Pro...
"%CSC%" -nologo -target:winexe -optimize+ -platform:anycpu -warn:4 ^
    -out:"%OUT_EXE%" ^
    -win32manifest:"%ROOT%src\app.manifest" ^
    -win32icon:"%ROOT%assets\app.ico" ^
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll -r:System.IO.Compression.dll -r:System.IO.Compression.FileSystem.dll ^
    -recurse:"%ROOT%src\*.cs"
if errorlevel 1 (
    echo [X] Build failed.
    goto :fail
)
copy /Y "%ROOT%src\app.config" "%OUT_EXE%.config" >nul

echo [OK] Built: %OUT_EXE%
if /i not "%~1"=="/quiet" pause
exit /b 0

:fail
if /i not "%~1"=="/quiet" pause
exit /b 1
