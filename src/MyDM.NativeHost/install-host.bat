@echo off
setlocal

echo === MyDM Native Messaging Host Installer ===
echo.

set "SCRIPT_DIR=%~dp0"
set "HOST_PATH=%SCRIPT_DIR%MyDM.NativeHost.exe"
set "CHROMIUM_MANIFEST_PATH=%SCRIPT_DIR%com.mydm.native.chromium.json"
set "FIREFOX_MANIFEST_PATH=%SCRIPT_DIR%com.mydm.native.firefox.json"
set "LEGACY_MANIFEST_PATH=%SCRIPT_DIR%com.mydm.native.json"

if not exist "%HOST_PATH%" (
    set "HOST_PATH=%SCRIPT_DIR%bin\Release\net8.0\win-x64\publish\MyDM.NativeHost.exe"
)
if not exist "%HOST_PATH%" (
    set "HOST_PATH=%SCRIPT_DIR%bin\Debug\net8.0\MyDM.NativeHost.exe"
)
if not exist "%HOST_PATH%" (
    set "HOST_PATH=%SCRIPT_DIR%..\MyDM.App\bin\Release\net8.0-windows\win-x64\publish\MyDM.NativeHost.exe"
)

if not exist "%HOST_PATH%" (
    echo ERROR: MyDM.NativeHost.exe not found.
    echo Build first: dotnet publish src\MyDM.NativeHost\MyDM.NativeHost.csproj -c Release -r win-x64 --self-contained true
    exit /b 1
)

set "DEFAULT_CHROMIUM_IDS=gnpallpkcdihlckdkddppkhgblokapdj"
set "DEFAULT_FIREFOX_IDS=mydm@mydm.app"
set "CHROMIUM_IDS=%~1"
set "FIREFOX_IDS=%~2"

if "%CHROMIUM_IDS%"=="" set "CHROMIUM_IDS=%DEFAULT_CHROMIUM_IDS%"
if "%FIREFOX_IDS%"=="" set "FIREFOX_IDS=%DEFAULT_FIREFOX_IDS%"

echo Chromium IDs: %CHROMIUM_IDS%
echo Firefox IDs : %FIREFOX_IDS%
echo Host path   : %HOST_PATH%

powershell -NoProfile -Command ^
    "$ErrorActionPreference = 'Stop';" ^
    "$hostPath = [System.IO.Path]::GetFullPath('%HOST_PATH%');" ^
    "$chromiumIds = '%CHROMIUM_IDS%' -split '[,;\s]+' | Where-Object { $_ -match '^[a-pA-P]{32}$' } | ForEach-Object { $_.ToLowerInvariant() } | Select-Object -Unique;" ^
    "$firefoxIds = '%FIREFOX_IDS%' -split '[,;\s]+' | Where-Object { $_ -match '^[A-Za-z0-9._@+\-]{3,}$' } | Select-Object -Unique;" ^
    "if ($chromiumIds.Count -eq 0) { throw 'No valid Chromium extension IDs provided.' }" ^
    "if ($firefoxIds.Count -eq 0) { throw 'No valid Firefox add-on IDs provided.' }" ^
    "$chromiumManifest = [ordered]@{" ^
    "  name = 'com.mydm.native';" ^
    "  description = 'MyDM Native Messaging Host';" ^
    "  path = $hostPath;" ^
    "  type = 'stdio';" ^
    "  allowed_origins = @($chromiumIds | ForEach-Object { \"chrome-extension://$_/\" })" ^
    "};" ^
    "$firefoxManifest = [ordered]@{" ^
    "  name = 'com.mydm.native';" ^
    "  description = 'MyDM Native Messaging Host';" ^
    "  path = $hostPath;" ^
    "  type = 'stdio';" ^
    "  allowed_extensions = @($firefoxIds)" ^
    "};" ^
    "$jsonOptions = @{ Depth = 6 };" ^
    "$chromiumManifest | ConvertTo-Json @jsonOptions | Set-Content -Path '%CHROMIUM_MANIFEST_PATH%' -Encoding UTF8;" ^
    "$firefoxManifest | ConvertTo-Json @jsonOptions | Set-Content -Path '%FIREFOX_MANIFEST_PATH%' -Encoding UTF8;" ^
    "$chromiumManifest | ConvertTo-Json @jsonOptions | Set-Content -Path '%LEGACY_MANIFEST_PATH%' -Encoding UTF8;"

if errorlevel 1 (
    echo ERROR: Failed to write native host manifest files.
    exit /b 1
)

echo Manifest created: %CHROMIUM_MANIFEST_PATH%
echo Manifest created: %FIREFOX_MANIFEST_PATH%

call :RegisterChromium "HKCU\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\Vivaldi\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\Opera Software\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\Opera Software\Opera Stable\NativeMessagingHosts\com.mydm.native"
call :RegisterChromium "HKCU\SOFTWARE\Opera Software\Opera GX Stable\NativeMessagingHosts\com.mydm.native"

REG ADD "HKCU\SOFTWARE\Mozilla\NativeMessagingHosts\com.mydm.native" /ve /t REG_SZ /d "%FIREFOX_MANIFEST_PATH%" /f >nul 2>&1
if errorlevel 1 (
    echo WARNING: Failed to register for Mozilla Firefox.
) else (
    echo Registered: HKCU\SOFTWARE\Mozilla\NativeMessagingHosts\com.mydm.native
)

echo.
echo ========================================
echo Native Messaging Host registration done.
echo Restart browser(s) for changes to take effect.
echo ========================================
exit /b 0

:RegisterChromium
REG ADD "%~1" /ve /t REG_SZ /d "%CHROMIUM_MANIFEST_PATH%" /f >nul 2>&1
if errorlevel 1 (
    echo WARNING: Failed to register %~1
) else (
    echo Registered: %~1
)
exit /b 0
