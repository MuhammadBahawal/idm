@echo off
setlocal

echo === MyDM Native Messaging Host Installer ===
echo.

set SCRIPT_DIR=%~dp0
set HOST_PATH=%SCRIPT_DIR%bin\Debug\net8.0\MyDM.NativeHost.exe
set MANIFEST_PATH=%SCRIPT_DIR%com.mydm.native.json

if not exist "%HOST_PATH%" (
    set HOST_PATH=%SCRIPT_DIR%MyDM.NativeHost.exe
)

if not exist "%HOST_PATH%" (
    echo ERROR: MyDM.NativeHost.exe not found.
    echo Build first: dotnet build MyDM.slnx
    exit /b 1
)

set DEFAULT_EXT_ID=gnpallpkcdihlckdkddppkhgblokapdj
set EXT_ID=%1
if "%EXT_ID%"=="" (
    set EXT_ID=%DEFAULT_EXT_ID%
    echo WARNING: No extension ID provided.
    echo          Using default ID: %DEFAULT_EXT_ID%
    echo          If browser popup shows disconnected, rerun with your real extension ID.
)

echo Using extension ID: %EXT_ID%
set ORIGIN="chrome-extension://%EXT_ID%/"
set HOST_PATH_ESCAPED=%HOST_PATH:\=\\%

powershell -NoProfile -Command ^
    "$manifest = @{" ^
    "    name = 'com.mydm.native';" ^
    "    description = 'MyDM Download Manager Native Messaging Host';" ^
    "    path = '%HOST_PATH_ESCAPED%';" ^
    "    type = 'stdio';" ^
    "    allowed_origins = @(%ORIGIN%)" ^
    "};" ^
    "$manifest | ConvertTo-Json | Set-Content -Path '%MANIFEST_PATH%' -Encoding UTF8"

if errorlevel 1 (
    echo ERROR: Failed to write manifest JSON.
    exit /b 1
)

echo Created manifest: %MANIFEST_PATH%

REG ADD "HKCU\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native" /ve /t REG_SZ /d "%MANIFEST_PATH%" /f >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to register for Google Chrome.
    exit /b 1
)
echo Registered for Google Chrome.

REG ADD "HKCU\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native" /ve /t REG_SZ /d "%MANIFEST_PATH%" /f >nul 2>&1
if errorlevel 1 (
    echo ERROR: Failed to register for Microsoft Edge.
    exit /b 1
)
echo Registered for Microsoft Edge.

echo.
echo ========================================
echo Native Messaging Host installed.
echo Restart your browser for changes to take effect.
echo ========================================
exit /b 0
