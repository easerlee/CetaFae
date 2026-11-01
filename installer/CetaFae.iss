#ifndef PayloadDir
  #error Supply /DPayloadDir with the self-contained publish directory
#endif
#ifndef OutputDir
  #error Supply /DOutputDir
#endif
#ifndef ChineseMessagesFile
  #error Supply /DChineseMessagesFile
#endif
#ifndef AppVersion
  #error Supply /DAppVersion
#endif

[Setup]
AppId={{57C7F34B-4F1C-49F1-93EE-995F8B12DB6D}
AppName=鲸汐 CetaFae
AppVersion={#AppVersion}
AppPublisher=CetaFae contributors
AppPublisherURL=https://github.com/easerlee/CetaFae
DefaultDirName={localappdata}\Programs\CetaFae
DefaultGroupName=鲸汐 CetaFae
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
UsePreviousAppDir=no
AllowNoIcons=no
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\CetaFae.Probe.exe
SetupIconFile=..\jingxi.ico
OutputDir={#OutputDir}
OutputBaseFilename=CetaFae-{#AppVersion}-win-x64-setup
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
SetupLogging=no

[Languages]
Name: "zh"; MessagesFile: "{#ChineseMessagesFile}"

[Files]
; First in the solid stream because the agreement helper is extracted before installation.
Source: "{#PayloadDir}\CetaFae.Probe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\plugins\*"; DestDir: "{app}\plugins"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\鲸汐 CetaFae"; Filename: "{app}\CetaFae.Probe.exe"; WorkingDir: "{app}"

[Code]
var
  AgreementPage: TWizardPage;
  AgreementText: TNewMemo;
  AcceptedScope: String;
  AgreementReady: Boolean;
  ExplicitlyAccepted: Boolean;
  InstallationCompleted: Boolean;

function InitializeSetup: Boolean;
begin
  { A simulated Next click in silent mode is not personal agreement. }
  Result := not WizardSilent;
end;

procedure InitializeWizard;
var
  ExitCode: Integer;
  Prefix: String;
  ScopeBytes: AnsiString;
begin
  AgreementPage := CreateCustomPage(wpWelcome, '欢迎使用鲸汐',
    '安装后直接拖动小鲸鱼求助。请先阅读使用协议与隐私说明。');
  AgreementText := TNewMemo.Create(WizardForm);
  AgreementText.Parent := AgreementPage.Surface;
  AgreementText.SetBounds(0, 0, AgreementPage.SurfaceWidth, AgreementPage.SurfaceHeight);
  AgreementText.ReadOnly := True;
  AgreementText.ScrollBars := ssVertical;
  AgreementText.WordWrap := True;
  ExtractTemporaryFile('CetaFae.Probe.exe');
  Prefix := ExpandConstant('{tmp}\ceta-agreement');
  AgreementReady := Exec(ExpandConstant('{tmp}\CetaFae.Probe.exe'),
    '--installation-info "' + Prefix + '"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
  if AgreementReady then AgreementReady := ExitCode = 0;
  if AgreementReady then AgreementText.Lines.LoadFromFile(Prefix + '.txt');
  if AgreementReady then AgreementReady := LoadStringFromFile(Prefix + '.scope', ScopeBytes);
  if AgreementReady then
  begin
    AcceptedScope := String(ScopeBytes);
    AgreementReady := Length(AcceptedScope) = 64;
  end;
  if not AgreementReady then
    AgreementText.Text := '无法读取当前连接配置或使用协议。尚未安装或记录同意。请修复配置后重新运行安装程序。';
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = AgreementPage.ID then
  begin
    WizardForm.NextButton.Caption := '同意协议并安装';
    WizardForm.NextButton.Width := ScaleX(138);
    WizardForm.NextButton.Left := WizardForm.CancelButton.Left - WizardForm.NextButton.Width - ScaleX(8);
    WizardForm.NextButton.Enabled := AgreementReady;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := (CurPageID <> AgreementPage.ID) or AgreementReady;
  if (CurPageID = AgreementPage.ID) and Result then ExplicitlyAccepted := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not ExplicitlyAccepted then RaiseException('尚未确认使用协议，未记录同意。');
    InstallationCompleted := Exec(ExpandConstant('{app}\CetaFae.Probe.exe'),
      '--complete-installation ' + AcceptedScope, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
    if InstallationCompleted then InstallationCompleted := ExitCode = 0;
    if not InstallationCompleted then
      RaiseException('文件已安装，但使用同意未保存或连接配置已变化。未启动鲸汐；请重新运行安装程序确认。');
  end;
  if (CurStep = ssDone) and InstallationCompleted then
    if not Exec(ExpandConstant('{app}\CetaFae.Probe.exe'), '', ExpandConstant('{app}'),
      SW_SHOWNORMAL, ewNoWait, ExitCode) then
      MsgBox('鲸汐已安装。可从开始菜单打开。', mbInformation, MB_OK);
end;
