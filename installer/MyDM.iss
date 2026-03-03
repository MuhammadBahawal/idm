#ifndef MyDMVersion
  #define MyDMVersion "1.0.0"
#endif

#ifndef MyDMSourceDir
  #error MyDMSourceDir define is required. Example: /DMyDMSourceDir=C:\path\to\bundle\MyDM
#endif

#ifndef ReleaseRoot
  #define ReleaseRoot "."
#endif

#define MyDMAppId "{{2A66A913-65B5-4736-8A55-64F1E7A1A4AF}"

[Setup]
AppId={#MyDMAppId}
AppName=MyDM Download Manager
AppVersion={#MyDMVersion}
AppPublisher=MyDM
DefaultDirName={localappdata}\Programs\MyDM
DefaultGroupName=MyDM Download Manager
DisableProgramGroupPage=yes
OutputDir={#ReleaseRoot}\installer
OutputBaseFilename=MyDMUserSetup-x64-{#MyDMVersion}
SetupIconFile={#SourcePath}\..\src\MyDM.App\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\MyDM.App.exe

[Files]
Source: "{#MyDMSourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MyDM Download Manager"; Filename: "{app}\MyDM.App.exe"
Name: "{autodesktop}\MyDM Download Manager"; Filename: "{app}\MyDM.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create desktop icon"; GroupDescription: "Additional icons:"

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Google\Chrome\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Vivaldi\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Opera Software\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Opera Software\Opera Stable\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Opera Software\Opera GX Stable\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.chromium.json"; Flags: uninsdeletekey
Root: HKCU; Subkey: "SOFTWARE\Mozilla\NativeMessagingHosts\com.mydm.native"; ValueType: string; ValueName: ""; ValueData: "{app}\com.mydm.native.firefox.json"; Flags: uninsdeletekey

[Run]
Filename: "{app}\MyDM.App.exe"; Description: "Launch MyDM"; Flags: nowait postinstall skipifsilent

[Code]
const
  DefaultChromiumExtensionId = 'gnpallpkcdihlckdkddppkhgblokapdj';
  DefaultFirefoxExtensionId = 'mydm@mydm.app';

function JsonEscape(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function BuildChromiumManifest(const HostPath: string): string;
begin
  Result :=
    '{' + #13#10 +
    '  "name": "com.mydm.native",' + #13#10 +
    '  "description": "MyDM Native Messaging Host",' + #13#10 +
    '  "path": "' + JsonEscape(HostPath) + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_origins": [' + #13#10 +
    '    "chrome-extension://' + DefaultChromiumExtensionId + '/"' + #13#10 +
    '  ]' + #13#10 +
    '}';
end;

function BuildFirefoxManifest(const HostPath: string): string;
begin
  Result :=
    '{' + #13#10 +
    '  "name": "com.mydm.native",' + #13#10 +
    '  "description": "MyDM Native Messaging Host",' + #13#10 +
    '  "path": "' + JsonEscape(HostPath) + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_extensions": [' + #13#10 +
    '    "' + DefaultFirefoxExtensionId + '"' + #13#10 +
    '  ]' + #13#10 +
    '}';
end;

procedure WriteNativeHostManifests;
var
  HostPath: string;
  ChromiumManifestPath: string;
  FirefoxManifestPath: string;
  LegacyManifestPath: string;
begin
  HostPath := ExpandConstant('{app}\MyDM.NativeHost.exe');
  ChromiumManifestPath := ExpandConstant('{app}\com.mydm.native.chromium.json');
  FirefoxManifestPath := ExpandConstant('{app}\com.mydm.native.firefox.json');
  LegacyManifestPath := ExpandConstant('{app}\com.mydm.native.json');

  SaveStringToFile(ChromiumManifestPath, BuildChromiumManifest(HostPath), False);
  SaveStringToFile(FirefoxManifestPath, BuildFirefoxManifest(HostPath), False);
  SaveStringToFile(LegacyManifestPath, BuildChromiumManifest(HostPath), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteNativeHostManifests;
  end;
end;
