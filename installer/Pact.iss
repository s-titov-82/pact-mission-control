#ifndef PactVersion
  #error PactVersion must be defined by Build-PactInstaller.ps1
#endif
#ifndef PactPayloadRoot
  #error PactPayloadRoot must be defined by Build-PactInstaller.ps1
#endif
#ifndef PactWebView2BootstrapperPath
  #error PactWebView2BootstrapperPath must be defined by Build-PactInstaller.ps1
#endif

#define PactAppName "PACT:> Mission Control"
#define PactAppId "PactMissionControl"

[Setup]
AppId={#PactAppId}
AppName={#PactAppName}
AppVersion={#PactVersion}
AppPublisher=s-titov-82
AppPublisherURL=https://github.com/s-titov-82/pact-mission-control
AppSupportURL=https://github.com/s-titov-82/pact-mission-control/issues
AppUpdatesURL=https://github.com/s-titov-82/pact-mission-control/releases
DefaultDirName={localappdata}\Programs\Pact Mission Control
DefaultGroupName={#PactAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
OutputDir={#PactOutputDirectory}
OutputBaseFilename={#PactOutputBaseFilename}
SetupIconFile={#PactIconPath}
UninstallDisplayIcon={app}\Pact.App.Avalonia.exe
LicenseFile={#PactLicensePath}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern dynamic
CloseApplications=yes
RestartApplications=no
UninstallLogMode=append
SetupLogging=yes
UninstallLogging=yes
#if PactSigned
SignTool=pact
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
english.DesktopIcon=Create a &desktop shortcut
russian.DesktopIcon=Создать ярлык на &рабочем столе

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PactWebView2BootstrapperPath}"; Flags: dontcopy noencryption
Source: "{#PactPayloadRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#PactAppName}"; Filename: "{app}\Pact.App.Avalonia.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#PactAppName}"; Filename: "{app}\Pact.App.Avalonia.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Pact.App.Avalonia.exe"; Description: "{cm:LaunchProgram,{#PactAppName}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
#include "Prerequisites.iss"
