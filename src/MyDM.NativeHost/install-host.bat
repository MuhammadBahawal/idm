@echo off
echo === MyDM Native Messaging Host Installer ===

:: Get the directory where this script is located
set SCRIPT_DIR=%~dp0
set HOST_PATH=%SCRIPT_DIR%MyDM.NativeHost.exe
set MANIFEST_PATH=%SCRIPT_DIR%com.mydm.native.json

:: Create the manifest
echo {> "%MANIFEST_PATH%"
echo   "name": "com.mydm.native",>> "%MANIFEST_PATH%"
echo   "description": "MyDM Download Manager Native Messaging Host",>> "%MANIFEST_PATH%"
echo   "path": "%HOST_PATH:\=\\%",>> "%MANIFEST_PATH%"
echo   "type": "stdio",>> "%MANIFEST_PATH%"
echo   "allowed_origins": [>> "%MANIFEST_PATH%"
echo     "chrome-extension://*/",>> "%MANIFEST_PATH%"
echo   ]>> "%MANIFEST_PATH%"
echo }>> "%MANIFEST_PATH%"

:: Register for Chrome
REG ADD "HKCU\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native" /ve /t REG_SZ /d "%MANIFEST_PATH%" /f
echo Registered for Google Chrome.

:: Register for Edge
REG ADD "HKCU\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native" /ve /t REG_SZ /d "%MANIFEST_PATH%" /f
echo Registered for Microsoft Edge.

echo.
echo Native Messaging Host registered successfully!
echo You may need to restart your browser for changes to take effect.
pause
