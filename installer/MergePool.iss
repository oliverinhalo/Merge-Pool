; MergePool installer (Inno Setup 6).
;
; Layout installed on the machine:
;   {app}\versions\{version}\   the binaries for one version, never modified after install
;   {app}\current               a directory junction pointing at the active version
;   {commonappdata}\MergePool\  config.json and logs, deliberately outside {app}
;
; An upgrade therefore only ever adds a version directory and repoints the junction, which is what
; lets MergePool.Updater roll back by pointing it at the previous version again.

#define AppName "MergePool"
#define AppPublisher "MergePool"
#define AppVersion "0.1.0"
#define ServiceName "MergePool"
#define WinFspUrl "https://github.com/winfsp/winfsp/releases/download/v2.0/winfsp-2.0.23075.msi"

[Setup]
AppId={{6F0B2A1C-4E9D-4E6A-9E4F-3D1F1D4E7C21}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=MergePool-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; Registering a service and writing under Program Files both need administrator rights.
PrivilegesRequired=admin
UninstallDisplayName={#AppName}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startservice"; Description: "Start the MergePool service when setup finishes"; GroupDescription: "Service"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked

[Files]
; Everything lands in a version directory. 'current' is created afterwards as a junction.
Source: "..\artifacts\publish\service\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\ui\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\updater\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\CHANGELOG.md"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion
; Downloaded at run time when WinFsp is missing.
Source: "{tmp}\winfsp.msi"; DestDir: "{tmp}"; Flags: external skipifsourcedoesntexist

[Dirs]
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\current\MergePool.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\current\MergePool.exe"; Tasks: desktopicon

[Run]
; WinFsp first: without it the service starts but cannot mount anything.
Filename: "msiexec.exe"; Parameters: "/i ""{tmp}\winfsp.msi"" /qn /norestart"; \
  StatusMsg: "Installing WinFsp…"; Check: NeedsWinFsp; Flags: waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "create {#ServiceName} binPath= ""{app}\current\MergePool.Service.exe"" start= auto DisplayName= ""MergePool Pool Engine"""; \
  StatusMsg: "Registering the MergePool service…"; Flags: runhidden waituntilterminated; Check: not ServiceExists

Filename: "{sys}\sc.exe"; Parameters: "description {#ServiceName} ""Serves MergePool's pooled drives. Stopping this service unmounts the pools; the files stay on their drives."""; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "failure {#ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000"; \
  Flags: runhidden waituntilterminated

Filename: "{sys}\sc.exe"; Parameters: "start {#ServiceName}"; \
  StatusMsg: "Starting the MergePool service…"; Flags: runhidden waituntilterminated; Tasks: startservice

Filename: "{app}\current\MergePool.exe"; Description: "Open MergePool"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stop and remove the service. Pool parts and their files are deliberately left on the drives.
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\current"
Type: filesandordirs; Name: "{app}\versions"

[Code]
var
  DownloadPage: TDownloadWizardPage;

function WinFspInstalled: Boolean;
begin
  { WinFsp registers its install directory; the driver DLL next to it is the thing we actually need. }
  Result :=
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\WinFsp') or
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WinFsp') or
    FileExists(ExpandConstant('{commonpf32}\WinFsp\bin\winfsp-x64.dll')) or
    FileExists(ExpandConstant('{commonpf}\WinFsp\bin\winfsp-x64.dll'));
end;

function NeedsWinFsp: Boolean;
begin
  Result := not WinFspInstalled;
end;

function ServiceExists: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#ServiceName}', '',
                 SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function StopServiceIfRunning: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if ServiceExists then
  begin
    { Stopping unmounts the pools cleanly. Nothing on the drives is touched. }
    Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(3000);
  end;
end;

{ Repoints {app}\current at the version just installed. Built beside the old link and swapped, so a
  failure part-way leaves either the old link or the new one, never a missing 'current'. }
function PointCurrentAt(Version: String): Boolean;
var
  ResultCode: Integer;
  CurrentPath, StagingPath, TargetPath: String;
begin
  CurrentPath := ExpandConstant('{app}\current');
  StagingPath := CurrentPath + '.new';
  TargetPath := ExpandConstant('{app}\versions\') + Version;

  if DirExists(StagingPath) then
    Exec(ExpandConstant('{sys}\cmd.exe'), '/c rmdir "' + StagingPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := Exec(ExpandConstant('{sys}\cmd.exe'),
                 '/c mklink /J "' + StagingPath + '" "' + TargetPath + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);

  if not Result then
    Exit;

  if DirExists(CurrentPath) then
    Exec(ExpandConstant('{sys}\cmd.exe'), '/c rmdir "' + CurrentPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  Result := Exec(ExpandConstant('{sys}\cmd.exe'),
                 '/c move "' + StagingPath + '" "' + CurrentPath + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if (CurPageID = wpReady) and NeedsWinFsp then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#WinFspUrl}', 'winfsp.msi', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        SuppressibleMsgBox(
          'MergePool could not download WinFsp:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
          'Install WinFsp from https://winfsp.dev and run this installer again.',
          mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { An upgrade must not overwrite files the running service has open. }
  StopServiceIfRunning;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not PointCurrentAt('{#AppVersion}') then
      SuppressibleMsgBox(
        'MergePool could not point {app}\current at version {#AppVersion}.' + #13#10 +
        'The files are installed; run MergePool.Updater --version {#AppVersion} to finish.',
        mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    SuppressibleMsgBox(
      'MergePool has been removed.' + #13#10#13#10 +
      'Your pooled files were not touched: each drive still holds them in its .PoolPart-{GUID} ' +
      'folder, in the same subfolders, readable without MergePool.',
      mbInformation, MB_OK, IDOK);
end;
