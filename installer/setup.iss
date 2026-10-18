; ResourceMonitoring-Setup — installer per-user, tanpa admin, dua bahasa (wizard mengikuti
; pilihan di layar; teks wizard memakai Default.isl / Indonesian.isl).
[Setup]
AppId={{7E3A9F0C-2D5B-4E8A-9C41-A6F0B2D3E517}}
AppName=Resource Monitoring
AppVersion=1.2.0
AppPublisher=SyaefulAxl
DefaultDirName={localappdata}\ResourceMonitoring
PrivilegesRequired=lowest
OutputDir=dist
OutputBaseFilename=ResourceMonitoring-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\rm-icon.ico
UninstallDisplayName=Resource Monitoring
UninstallDisplayIcon={app}\ResourceMonitoring.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "indonesian"; MessagesFile: "Indonesian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}:"
Name: "autostart"; Description: "Jalankan saat Windows menyala / Run when Windows starts"; GroupDescription: "Opsi / Options:"

[Files]
Source: "..\ResourceMonitoring.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Resource Monitoring\Resource Monitoring"; Filename: "{app}\ResourceMonitoring.exe"
Name: "{userdesktop}\Resource Monitoring"; Filename: "{app}\ResourceMonitoring.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ResourceMonitoring"; ValueData: """{app}\ResourceMonitoring.exe"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\ResourceMonitoring.exe"; Description: "{cm:LaunchProgram,Resource Monitoring}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\cmd.exe"; Parameters: "/C schtasks /Delete /TN ResourceMonitoring-TempSvc /F & exit /b 0"; Flags: runhidden
