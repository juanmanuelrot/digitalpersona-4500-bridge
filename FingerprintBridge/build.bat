@echo off
echo ============================================
echo  Fingerprint Bridge - Build Script
echo ============================================
echo.

:: Check for .NET SDK
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET 8 SDK not found. Install from https://dot.net
    exit /b 1
)

:: Check DPUruNet.dll
if not exist "lib\DPUruNet.dll" (
    echo.
    echo WARNING: lib\DPUruNet.dll not found!
    echo.
    echo Please copy DPUruNet.dll from your DigitalPersona SDK installation:
    echo   Typical path: C:\Program Files\DigitalPersona\Bin\DotNetApi\DPUruNet.dll
    echo   Copy it to:   %~dp0lib\DPUruNet.dll
    echo.
    pause
    exit /b 1
)

echo [1/3] Restoring dependencies...
dotnet restore

echo.
echo [2/3] Building Release...
dotnet build -c Release

echo.
echo [3/3] Publishing self-contained exe...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o bin\Release\net8.0-windows\win-x64\publish

echo.
echo ============================================
echo  Build complete!
echo  Output: bin\Release\net8.0-windows\win-x64\publish\FingerprintBridge.exe
echo ============================================
echo.
echo To create the installer, open installer\setup.iss with Inno Setup and compile.
pause
