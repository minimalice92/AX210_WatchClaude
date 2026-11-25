; AX210_WatchClaude installer (Inno Setup 6.4+). Built by ..\build.ps1, which passes /DAppVersion.
; Light/dark follows Windows, like the app. Installs per machine (the watchdog needs admin rights to change
; adapter settings) and registers a scheduled task so it starts elevated at sign-in without a UAC prompt.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "AX210_WatchClaude"
#define AppExe "AX210_WatchClaude.exe"
#define AppUrl "https://github.com/minimalice92/AX210_WatchClaude"

[Setup]
AppId={{B59B1DE1-262F-47DF-B9C3-7EE4DB1C334C}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=minimalice92
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppComments=Wi-Fi watchdog for Intel AX210 cards: keeps gaming connections steady.
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
; one page: the options, then Install (a different folder can still be chosen with /DIR=)
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
DisableWelcomePage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern dynamic windows11
WizardSizePercent=100
WizardImageFile=..\assets\wizard-164x314.bmp,..\assets\wizard-246x471.bmp,..\assets\wizard-328x628.bmp
WizardSmallImageFile=..\assets\wizard-small-55.bmp,..\assets\wizard-small-83.bmp,..\assets\wizard-small-110.bmp
WizardImageFileDynamicDark=..\assets\wizard-164x314.bmp,..\assets\wizard-246x471.bmp,..\assets\wizard-328x628.bmp
WizardSmallImageFileDynamicDark=..\assets\wizard-small-55.bmp,..\assets\wizard-small-83.bmp,..\assets\wizard-small-110.bmp
WizardImageAlphaFormat=defined
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
WizardSelectTasks=Install options
SelectTasksDesc=Choose how AX210_WatchClaude starts.
SelectTasksLabel2=AX210_WatchClaude will be installed to Program Files. Choose your options, then click Install.
ButtonNext=&Install
FinishedHeadingLabel=AX210_WatchClaude is installed
FinishedLabelNoIcons=It runs in the tray (the Wi-Fi icon by the clock). The first time it opens, a short setup assistant checks your adapter and network and fills in the settings.
FinishedLabel=It runs in the tray (the Wi-Fi icon by the clock). The first time it opens, a short setup assistant checks your adapter and network and fills in the settings.

[Tasks]
Name: "autostart"; Description: "&Start automatically with Windows (recommended)"
Name: "desktopicon"; Description: "Create a &desktop shortcut"

[InstallDelete]
; files from the earlier script-based install
Type: files; Name: "{app}\uninstall.ps1"
Type: files; Name: "{app}\install.ps1"
Type: files; Name: "{app}\README.md"

[Files]
Source: "..\build\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Wi-Fi watchdog for gaming"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Wi-Fi watchdog for gaming"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Parameters: "--register-task --autostart"; StatusMsg: "Setting up the background task…"; Flags: runhidden waituntilterminated; Tasks: autostart
Filename: "{app}\{#AppExe}"; Parameters: "--register-task"; StatusMsg: "Setting up the background task…"; Flags: runhidden waituntilterminated; Tasks: not autostart
Filename: "{app}\{#AppExe}"; Description: "Open AX210_WatchClaude"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  RestoreSettings, DeleteData: Boolean;

function InitializeSetup(): Boolean;
var Release: Cardinal;
begin
  Result := True;
  if not RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) or (Release < 528040) then
  begin
    SuppressibleMsgBox('AX210_WatchClaude needs the .NET Framework 4.8, which is part of Windows 10 (May 2019 Update and later) and Windows 11.' + #13#10#13#10 +
      'Please run Windows Update, then try again.', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

{ Stop a running copy (and the old task, so it can't restart it mid-install) before files are replaced. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer; Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn "{#AppName}" /f', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if FileExists(Exe) then Exec(Exe, '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

{ Uninstall options: one small dialog after "Are you sure?" }
procedure AskUninstallOptions();
var
  Form: TSetupForm; Info: TNewStaticText; CbRestore, CbPurge: TNewCheckBox; BOk: TNewButton; Y: Integer;
begin
  Form := CreateCustomForm(ScaleX(400), ScaleY(178), False, True);
  try
    Form.Caption := 'Uninstall AX210_WatchClaude';
    Y := ScaleY(16);
    Info := TNewStaticText.Create(Form); Info.Parent := Form; Info.AutoSize := False; Info.WordWrap := True;
    Info.SetBounds(ScaleX(18), Y, Form.ClientWidth - ScaleX(36), ScaleY(30));
    Info.Caption := 'Choose what to do with the changes AX210_WatchClaude made:';
    Y := Y + ScaleY(32);
    CbRestore := TNewCheckBox.Create(Form); CbRestore.Parent := Form;
    CbRestore.SetBounds(ScaleX(18), Y, Form.ClientWidth - ScaleX(36), ScaleY(22));
    CbRestore.Caption := 'Put my Wi-Fi adapter and DNS settings back the way they were';
    CbRestore.Checked := True;
    Y := Y + ScaleY(26);
    CbPurge := TNewCheckBox.Create(Form); CbPurge.Parent := Form;
    CbPurge.SetBounds(ScaleX(18), Y, Form.ClientWidth - ScaleX(36), ScaleY(22));
    CbPurge.Caption := 'Also delete its settings, logs and saved driver copies';
    CbPurge.Checked := False;
    BOk := TNewButton.Create(Form); BOk.Parent := Form;
    BOk.SetBounds(Form.ClientWidth - ScaleX(18) - ScaleX(96), Form.ClientHeight - ScaleY(16) - ScaleY(28), ScaleX(96), ScaleY(28));
    BOk.Caption := 'Continue'; BOk.ModalResult := mrOk; BOk.Default := True;
    Form.ActiveControl := BOk;
    Form.ShowModal();
    RestoreSettings := CbRestore.Checked;
    DeleteData := CbPurge.Checked;
  finally
    Form.Free();
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var Code: Integer; Exe: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RestoreSettings := True; DeleteData := False;
    if not UninstallSilent then AskUninstallOptions();
    Exe := ExpandConstant('{app}\{#AppExe}');
    UninstallProgressForm.StatusLabel.Caption := 'Stopping AX210_WatchClaude…';
    Exec(Exe, '--unregister-task', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Exec(Exe, '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
    if RestoreSettings then
    begin
      UninstallProgressForm.StatusLabel.Caption := 'Restoring your Wi-Fi settings (Wi-Fi may drop for a few seconds)…';
      Exec(Exe, '--restore-original', '', SW_HIDE, ewWaitUntilTerminated, Code);
    end
    else
      Exec(Exe, '--cleanup', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if (CurUninstallStep = usPostUninstall) and DeleteData then
  begin
    DelTree(ExpandConstant('{commonappdata}\{#AppName}'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
  end;
end;
