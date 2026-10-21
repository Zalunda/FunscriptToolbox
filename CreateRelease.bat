@echo off
setlocal enabledelayedexpansion

cd /d "%~dp0"

echo ========================================================
echo   FunscriptToolbox - Create Release Package
echo ========================================================
echo.

:: Check for custom version parameter (e.g. CreateRelease.bat 2.3.0)
if not "%~1"=="" (
    echo Applying version: %~1
    powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\set-version.ps1" -Version "%~1"
    if !ERRORLEVEL! neq 0 (
        echo [ERROR] Failed to set version.
        pause
        exit /b !ERRORLEVEL!
    )
    echo.
)

:: Locate MSBuild via vswhere or PATH
set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" (
    for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do (
        set "MSBUILD=%%i"
    )
)

if "%MSBUILD%"=="" (
    where msbuild >nul 2>nul
    if %ERRORLEVEL% equ 0 (
        set "MSBUILD=msbuild"
    )
)

if "%MSBUILD%"=="" (
    echo [ERROR] MSBuild was not found. Please install Visual Studio or Build Tools with C++ and .NET workloads.
    pause
    exit /b 1
)

echo Using MSBuild: "!MSBUILD!"
echo.

:: Build solution in Release configuration
echo Compiling solution (Release ^| Any CPU)...
"!MSBUILD!" "src\FunscriptToolbox.sln" /p:Configuration=Release /p:Platform="Any CPU" /m /v:minimal
if !ERRORLEVEL! neq 0 (
    echo.
    echo [ERROR] Compilation failed. See errors above.
    pause
    exit /b !ERRORLEVEL!
)

:: Package into release zip
echo.
echo Packaging release zip...
set "VER_ARG="
if not "%~1"=="" (
    set VER_ARG=-Version "%~1"
)

powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\package-release.ps1" -Configuration Release !VER_ARG!
if !ERRORLEVEL! neq 0 (
    echo.
    echo [ERROR] Packaging failed.
    pause
    exit /b !ERRORLEVEL!
)

echo.
echo ========================================================
echo   Release build complete!
echo   Zip file(s) available in: %~dp0dist\
echo ========================================================
echo.
pause
