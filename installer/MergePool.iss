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
#ifndef AppVersion
  ; build.ps1 passes /DAppVersion from Directory.Build.props. This is only the fallback for
  ; compiling the script straight from the Inno Setup IDE.
  #define AppVersion "0.3.0"
#endif
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
Name: "startup"; Description: "Open MergePool in the notification area when I sign in"; GroupDescription: "Shortcuts"

[Files]
; Everything lands in a version directory. 'current' is created afterwards as a junction.
Source: "..\artifacts\publish\service\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\ui\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\publish\updater\*"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\CHANGELOG.md"; DestDir: "{app}\versions\{#AppVersion}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\current\MergePool.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\current\MergePool.exe"; Tasks: desktopicon
; Started with --tray so signing in does not throw a window in the user's face. The pools are
; mounted by the service regardless; this is only about having the window within reach.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\current\MergePool.exe"; Parameters: "--tray"; Tasks: startup

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
  StatusMsg: "Starting the MergePool service…"; Flags: runhidden waituntilterminated; Check: ShouldStartService

Filename: "{app}\current\MergePool.exe"; Description: "Open MergePool"; \
  Flags: postinstall nowait skipifsilent

[UninstallRun]
; Stop and remove the service. Pool parts and their files are deliberately left on the drives.
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\current"
Type: filesandordirs; Name: "{app}\versions"
Type: files; Name: "{userstartup}\{#AppName}.lnk"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  // Set while installing: true when MergePool was already installed, i.e. this is an upgrade and
  // PrepareToInstall stopped a service that has to be put back.
  UpgradingExistingInstall: Boolean;

function WinFspInstalled: Boolean;
begin
  // WinFsp registers its install directory; the driver DLL next to it is what we actually need.
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

procedure StopServiceIfRunning;
var
  ResultCode: Integer;
begin
  UpgradingExistingInstall := ServiceExists;
  if not UpgradingExistingInstall then
    Exit;

  // Stopping unmounts the pools cleanly. Nothing on the drives is touched.
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // sc only asks the service to stop; give it time to release the files about to be replaced.
  Sleep(5000);
end;

// An upgrade must put back the service it stopped. Leaving that to the task checkbox meant an
// upgrade could stop the engine and never start it again, which looks exactly like MergePool
// being broken: the UI comes up and says the service cannot be reached.
function ShouldStartService: Boolean;
begin
  Result := UpgradingExistingInstall or WizardIsTaskSelected('startservice');
end;

// Repoints {app}\current at the version just installed. The service's binary path goes through
// this link, so if it does not end up resolving, MergePool is dead on the next start.
// Note: these are // comments on purpose. A Pascal { } comment would end at the } in {app}.

// rmdir without /S removes a junction itself and never the directory it points at.
procedure RemoveLink(LinkPath: String);
var
  ResultCode: Integer;
begin
  if DirExists(LinkPath) then
    Exec(ExpandConstant('{cmd}'), '/c rmdir "' + LinkPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function CreateJunction(LinkPath, TargetPath: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/c mklink /J "' + LinkPath + '" "' + TargetPath + '"',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function PointCurrentAt(Version: String): Boolean;
var
  CurrentPath, StagingPath, TargetPath: String;
begin
  CurrentPath := ExpandConstant('{app}\current');
  StagingPath := CurrentPath + '.new';
  TargetPath := ExpandConstant('{app}\versions\') + Version;

  // Build the new link beside the old one and swap, so a crash part-way leaves one or the other.
  RemoveLink(StagingPath);

  if CreateJunction(StagingPath, TargetPath) then
  begin
    RemoveLink(CurrentPath);

    if not RenameFile(StagingPath, CurrentPath) then
    begin
      // The swap failed with the old link already gone, which would leave the service pointing at
      // nothing. Put a working link back directly rather than leave it missing.
      RemoveLink(StagingPath);
      CreateJunction(CurrentPath, TargetPath);
    end;
  end
  else
  begin
    RemoveLink(CurrentPath);
    CreateJunction(CurrentPath, TargetPath);
  end;

  // Whether the link resolves to a runnable service is the only thing that actually matters.
  Result := FileExists(AddBackslash(CurrentPath) + 'MergePool.Service.exe');
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
  // An upgrade must not overwrite files the running service has open.
  StopServiceIfRunning;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not PointCurrentAt('{#AppVersion}') then
      SuppressibleMsgBox(
        'MergePool could not point ' + ExpandConstant('{app}\current') +
        ' at version {#AppVersion}.' + #13#10 +
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
