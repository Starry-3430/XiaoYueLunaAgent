; Luna 安装程序脚本（Inno Setup 6.5+）
; 构建：pwsh .\scripts\build-installer.ps1

#define MyAppName "Luna"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0.0"
#endif
#define MyAppPublisher "Starry"
#define MyAppExeName "Luna.exe"
#define MyAppId "{{2C0E7A8B-06FE-4B8D-8744-3DF8FA42CA72}"
#define MyAppUrl "https://github.com/Starry-3430/XiaoYueLunaAgent"

[Setup]
WizardSmallImageFile=
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\Luna
DefaultGroupName=Luna
DisableProgramGroupPage=yes
OutputDir=..\publish\installer
OutputBaseFilename=Luna-v{#MyAppVersion}-Setup
SetupIconFile=..\Luna\Assets\Luna.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE
InfoBeforeFile=..\THIRD-PARTY-NOTICES.md
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\Luna-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Luna"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Luna"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Luna}"; Flags: nowait postinstall

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 清理应用写入的开机自启项（如果用户开启过）。
    RegDeleteValue(HKEY_CURRENT_USER,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Luna');

    // 管理员自启动使用计划任务；普通权限下删除失败不影响卸载。
    Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/Delete /TN "Luna" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    // 用户数据默认保留；仅在用户明确确认后删除。
    if SuppressibleMsgBox(
      '是否同时删除 Luna 的个人数据（会话、日记、附件、设置）？' + #13#10 +
      '此操作不可恢复。',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Luna'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Luna'), True, True, True);
    end;
  end;
end;
