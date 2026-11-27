; Inno Setup script for Lumina Recorder
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\src\LuminaRecorder\bin\x64\Release\net48"
#endif

[Setup]
AppId={{6D3B5E2A-8C41-4F7B-9E1D-2A7C5B9F4E10}
AppName=Lumina Recorder
AppVersion={#AppVersion}
AppPublisher=Lumina Recorder
DefaultDirName={autopf}\Lumina Recorder
DefaultGroupName=Lumina Recorder
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=LuminaRecorder-Setup-{#AppVersion}
SetupIconFile=..\src\LuminaRecorder\Assets\app.ico
UninstallDisplayIcon={app}\LuminaRecorder.exe
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Excludes: "*.pdb,*.xml"
Source: "vc_redist.x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{autoprograms}\Lumina Recorder"; Filename: "{app}\LuminaRecorder.exe"
Name: "{autoprograms}\Lumina Recorder (Safe mode)"; Filename: "{app}\LuminaRecorder.exe"; Parameters: "--software-render"
Name: "{autodesktop}\Lumina Recorder"; Filename: "{app}\LuminaRecorder.exe"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Visual C++ runtime..."; Flags: waituntilterminated
Filename: "{app}\LuminaRecorder.exe"; Description: "{cm:LaunchProgram,Lumina Recorder}"; Flags: nowait postinstall skipifsilent
