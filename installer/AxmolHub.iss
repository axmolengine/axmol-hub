#define HubVersion "0.1.6"
#ifndef HubAppId
  #define HubAppId "{{6917FD80-56A9-4E98-B327-A56D8CCE7641}"
#endif
#ifndef HubAppName
  #define HubAppName "Axmol Hub"
#endif
#ifndef HubSetupName
  #define HubSetupName "AxmolHub-0.1.6-win-x64-setup"
#endif
#ifndef HubInstallFolder
  #define HubInstallFolder "AxmolHub"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\app"
#endif
[Setup]
AppId={#HubAppId}
AppName={#HubAppName}
AppVersion={#HubVersion}
DefaultDirName={localappdata}\Programs\{#HubInstallFolder}
DefaultGroupName={#HubAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\AxmolHub.App.exe
SetupIconFile=..\src\AxmolHub.App\Assets\hub-icon.ico
OutputDir=..\artifacts\installer
OutputBaseFilename={#HubSetupName}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
[Languages]
Name: "zhCN"; MessagesFile: "ChineseSimplified.isl"
Name: "enUS"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked
[Files]
; 不打包任何引擎、工具链、调试 CRT 或用户设置；卸载器也不会删除运行时数据。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "hub-settings.json,data\*,*.pdb"
[Icons]
Name: "{userprograms}\{#HubAppName}"; Filename: "{app}\AxmolHub.App.exe"
Name: "{userdesktop}\{#HubAppName}"; Filename: "{app}\AxmolHub.App.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\AxmolHub.App.exe"; Description: "{cm:LaunchProgram,Axmol Hub}"; Flags: nowait postinstall skipifsilent
[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not FileExists(ExpandConstant('{app}\hub-settings.json')) then
  begin
    if ActiveLanguage = 'enUS' then
      SaveStringToFile(ExpandConstant('{app}\hub-settings.json'), '{"Language":"en-US"}', False)
    else
      SaveStringToFile(ExpandConstant('{app}\hub-settings.json'), '{"Language":"zh-CN"}', False);
  end;
end;
