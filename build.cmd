@echo off
rem  ASCII only: cmd.exe reads .cmd files in the console OEM codepage,
rem  so any non-ASCII text here would be mangled into invalid commands.
setlocal
cd /d "%~dp0"

echo.
echo   Building MftProbe into a single portable exe...
echo.

where dotnet > nul 2>&1
if errorlevel 1 (
    echo   ERROR: dotnet not found. Install the .NET 10 SDK:
    echo       winget install Microsoft.DotNet.SDK.10
    echo.
    pause
    exit /b 1
)

dotnet --list-sdks | findstr /b "10." > nul
if errorlevel 1 (
    echo   ERROR: runtimes are installed but SDK 10 is not. The SDK is required:
    echo       winget install Microsoft.DotNet.SDK.10
    echo.
    dotnet --list-sdks
    echo.
    pause
    exit /b 1
)

rem  self-contained + single-file: the runtime is packed inside, nothing to install.
rem  Trimming is deliberately off - it strips anything reachable only via reflection,
rem  which is a poor trade on unproven code. Enable it once the core is settled.
dotnet publish "tools\MftProbe\MftProbe.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "dist"

if errorlevel 1 (
    echo.
    echo   Build failed.
    echo.
    pause
    exit /b 1
)


echo.
echo   Building the file manager (TerminalExplorer.exe)...
echo.
dotnet publish "src\TerminalExplorer.App\TerminalExplorer.App.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "dist"

if errorlevel 1 (
    echo.
    echo   File manager build failed.
    echo.
    pause
    exit /b 1
)

echo.
echo   Done: dist\MftProbe.exe
echo.
echo   Run it by double-clicking. Windows will ask for administrator rights -
echo   reading the MFT is impossible without them.
echo.
pause
