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
AppMutex=Global\Luna_SingleInstance_Mutex
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
InfoBeforeFile=..\licenses\THIRD-PARTY-NOTICES.txt
VersionInfoVersion={#MyAppVersion}

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.PrivacyStatementTitle=隐私安全声明
chinesesimp.PrivacyStatementDescription=请阅读以下隐私安全声明，勾选同意后继续安装。
chinesesimp.PrivacyStatementAccept=我已阅读并同意上述隐私安全声明
chinesesimp.PrivacyStatementRequired=请先勾选“我已阅读并同意上述隐私安全声明”后再继续。
chinesesimp.PrivacyStatementFile=PrivacyStatement-ZH.txt
english.PrivacyStatementTitle=Privacy and Security Statement
english.PrivacyStatementDescription=Please read the privacy and security statement below, then check the box to continue.
english.PrivacyStatementAccept=I have read and agree to the privacy and security statement above
english.PrivacyStatementRequired=Please check "I have read and agree to the privacy and security statement above" to continue.
english.PrivacyStatementFile=PrivacyStatement-EN.txt

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\Luna-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 隐私安全声明：编译进安装包，供自定义向导页按当前语言读取。
Source: "..\publish\Luna-win-x64\licenses\Luna智能助手软件 V1.0 隐私和安全说明.txt"; DestDir: "{tmp}"; DestName: "PrivacyStatement-ZH.txt"; Flags: dontcopy
Source: "..\publish\Luna-win-x64\licenses\Luna Smart Assistant Software V1.0 - Privacy and Security Statement (EN).txt"; DestDir: "{tmp}"; DestName: "PrivacyStatement-EN.txt"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Luna"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Luna"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Luna}"; Flags: nowait postinstall

[Code]
var
  PrivacyPage: TWizardPage;
  PrivacyMemo: TNewMemo;
  PrivacyCheckBox: TNewCheckBox;
  PrivacyFileName: String;

procedure PrivacyCheckBoxClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := PrivacyCheckBox.Checked;
end;

procedure LoadPrivacyStatement;
var
  Lines: TArrayOfString;
  I: Integer;
  Content: String;
begin
  ExtractTemporaryFile(PrivacyFileName);

  Content := '';
  if LoadStringsFromFile(ExpandConstant('{tmp}\' + PrivacyFileName), Lines) then
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
      Content := Content + Lines[I] + #13#10;
  end;

  PrivacyMemo.Text := Content;
end;

procedure InitializeWizard();
begin
  PrivacyFileName := ExpandConstant('{cm:PrivacyStatementFile}');

  PrivacyPage := CreateCustomPage(wpLicense,
    ExpandConstant('{cm:PrivacyStatementTitle}'),
    ExpandConstant('{cm:PrivacyStatementDescription}'));

  PrivacyMemo := TNewMemo.Create(PrivacyPage);
  PrivacyMemo.Parent := PrivacyPage.Surface;
  PrivacyMemo.Left := 0;
  PrivacyMemo.Top := 0;
  PrivacyMemo.Width := PrivacyPage.SurfaceWidth;
  PrivacyMemo.Height := PrivacyPage.SurfaceHeight - 32;
  PrivacyMemo.ReadOnly := True;
  PrivacyMemo.ScrollBars := ssVertical;
  PrivacyMemo.WordWrap := True;

  PrivacyCheckBox := TNewCheckBox.Create(PrivacyPage);
  PrivacyCheckBox.Parent := PrivacyPage.Surface;
  PrivacyCheckBox.Left := 0;
  PrivacyCheckBox.Top := PrivacyMemo.Height + 8;
  PrivacyCheckBox.Width := PrivacyPage.SurfaceWidth;
  PrivacyCheckBox.Caption := ExpandConstant('{cm:PrivacyStatementAccept}');
  PrivacyCheckBox.OnClick := @PrivacyCheckBoxClick;

  LoadPrivacyStatement;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PrivacyPage.ID then
  begin
    if not PrivacyCheckBox.Checked then
    begin
      MsgBox(ExpandConstant('{cm:PrivacyStatementRequired}'), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = PrivacyPage.ID then
    WizardForm.NextButton.Enabled := PrivacyCheckBox.Checked;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  // 卸载会删除程序文件，且可能同时删除个人数据。Luna 仍在运行时，
  // 被占用的文件无法删除，会造成程序目录和用户数据只被删掉一部分。
  if CheckForMutexes('Global\Luna_SingleInstance_Mutex') then
  begin
    MsgBox('Luna 正在运行。请先从托盘菜单退出 Luna，再重新执行卸载。',
      mbError, MB_OK);
    Result := False;
  end;
end;

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
