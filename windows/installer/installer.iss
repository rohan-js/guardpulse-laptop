; GuardPulse Laptop — Inno Setup installer
; Replaces install.ps1: bundles the merged ReadyToRun payload, collects Firebase
; credentials in the wizard, and configures the service, Safe Boot keys, ACLs,
; and Run-key fallback — all elevated, all silent.

#ifndef AppVersion
#define AppVersion "0.2.38"
#endif
#ifndef AppVersionCode
#define AppVersionCode "38"
#endif
#define AppName "Device Service"
#define ServiceName "GuardPulseDeviceService"

; The Firebase web API key is NOT committed to the repo. At build time the
; compiler reads it from the gitignored firebase-local.iss (see
; firebase-local.example.iss) so local builds keep producing a working
; agent-config.json; without it the wizard pre-fills a placeholder instead.
#ifexist "firebase-local.iss"
#include "firebase-local.iss"
#else
#define FirebaseApiKey "REPLACE_WITH_YOUR_FIREBASE_API_KEY"
#endif

[Setup]
AppId={{7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34}}
AppName={#AppName}
AppVersion={#AppVersion}
; De-branded: no publisher (keeps CompanyName blank on both exes).
DefaultDirName={autopf}\Device Service
; Staging location only: Inno writes the uninstaller here, then HideUninstaller
; moves it to a random per-install ProgramData folder (unpredictable path) and
; deletes this directory — so no unins files remain under the app or state dirs.
UninstallFilesDir={commonappdata}\GuardPulse\Laptop\sys
PrivilegesRequired=admin
OutputBaseFilename=DeviceServiceSetup-{#AppVersion}
SetupIconFile=..\..\docs\assets\neutral.ico
WizardStyle=modern
SolidCompression=yes
Compression=lzma2/max
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\GuardPulse.Agent.Session.exe
; Neutral version metadata (patched into BOTH the setup exe and the uninstaller;
; FileDescription/FileVersion on the uninstaller are later stripped entirely).
VersionInfoDescription=System service
VersionInfoCompany=
VersionInfoProductName=
VersionInfoProductTextVersion=
VersionInfoCopyright=
VersionInfoOriginalFileName=
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; Surface the honest limit where the parent will actually read it.
FinishedLabelNoIcons=Setup has finished installing Device Service.%n%nIMPORTANT: for full protection, the child's Windows account must be a STANDARD user (Settings > Accounts > Family or other users > Change account type). An administrator account can remove any software - this installer's PIN gate, self-repair sentinel and tamper alerts make removal hard and loud, but only a standard account makes it stoppable.%n%nWrong-PIN uninstall attempts and tamper events are reported to the parent app.

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "agent-config.template.json"; DestDir: "{app}"; Flags: ignoreversion
; Self-repair sentinel: deployed OUTSIDE {app} (System32\GuardPulse) so it
; survives deletion of the app directory; registered as a hidden SYSTEM task.
Source: "sentinel.ps1"; DestDir: "{sys}\GuardPulse"; Flags: ignoreversion

[UninstallDelete]
; Removes runtime-created files (agent-config.json, local logs) on uninstall.
Type: filesandordirs; Name: "{app}"
; Removes stale install trees from the OTHER Program Files variant (the x86 vs
; native split — see RemoveStaleInstall); {app} itself is removed above.
Type: filesandordirs; Name: "{commonpf}\Device Service"
Type: filesandordirs; Name: "{commonpf64}\Device Service"
; Stale dashboard shortcuts from pre-0.2.13 installs (the local web dashboard is
; removed; nothing creates these anymore — this only cleans them up).
Type: files; Name: "{commondesktop}\GuardPulse Dashboard.url"
Type: files; Name: "{commonprograms}\GuardPulse\Dashboard.url"
Type: files; Name: "{userdesktop}\GuardPulse Dashboard.url"
[Dirs]
Name: "{commonappdata}\GuardPulse\Laptop\logs"
; Hide the app folder so Explorer doesn't advertise it (same trick Windows uses
; for e.g. ProgramData). Applies at install; an explicit attrib call in
; ssPostInstall re-applies it on upgrades where the folder already exists.
Name: "{app}"; Attribs: hidden

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{#ServiceName}"; ValueType: string; ValueName: ""; ValueData: "Service"; Flags: uninsdeletekey noerror
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{#ServiceName}"; ValueType: string; ValueName: ""; ValueData: "Service"; Flags: uninsdeletekey noerror
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeviceServiceAgent"; ValueData: """{app}\GuardPulse.Agent.Session.exe"""; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\net.exe"; Parameters: "start {#ServiceName}"; Flags: runhidden skipifdoesntexist

[UninstallRun]
; Process/service teardown lives in CurUninstallStepChanged(usUninstall) via
; StopAgentStack (Exec, ignored result codes) — usUninstall runs before this
; section, and [UninstallDelete] runs last, which is the required order.

[Code]
var
  FirebasePage: TInputQueryWizardPage;
  // Uninstall PIN gate (shown by the uninstaller, not this installer).
  // SentinelRegistered tracks whether ssPostInstall deployed the self-repair
  // task, so the uninstaller knows whether to attempt its removal.
  SentinelDeployed: Boolean;
  // Previous install's uninstaller folder, captured at ssInstall (before Inno
  // rewrites the ARP registration) and cleaned up at ssPostInstall.
  OldUninstDir: String;
  // Final path of the hidden uninstaller exe, set by HideUninstaller so
  // StripUninstallerMetadata can delete its version resource afterwards.
  HiddenUninstExe: String;

procedure InitializeWizard;
begin
  FirebasePage := CreateInputQueryPage(wpSelectDir,
    'Firebase Configuration', 'Enter your Firebase project credentials.',
    'These values are written to agent-config.json. All fields are required.');
  FirebasePage.Add('Web API Key:', False);
  FirebasePage.Add('Project ID:', False);
  FirebasePage.Add('Database URL:', False);
  // Pre-filled with the GuardPulse laptop project so a silent install
  // (/VERYSILENT) and a click-through wizard both produce a working agent-config.
  // The API key comes from the build-time define (gitignored firebase-local.iss).
  FirebasePage.Values[0] := '{#FirebaseApiKey}';
  FirebasePage.Values[1] := 'guardpulse-laptop-sg';
  FirebasePage.Values[2] := 'https://guardpulse-laptop-sg-default-rtdb.asia-southeast1.firebasedatabase.app';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ProjectId, DatabaseUrl: string;
begin
  Result := True;
  if CurPageID = FirebasePage.ID then
  begin
    if Length(Trim(FirebasePage.Values[0])) = 0 then
    begin
      MsgBox('API Key is required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    // Coherence guard: the database URL's first hostname label must be the
    // project id. A mismatched triple (e.g. a US-project key with a Singapore
    // URL) parses fine and then fails EVERY cloud write silently — reject it
    // at install time instead.
    ProjectId := Trim(FirebasePage.Values[1]);
    DatabaseUrl := Trim(FirebasePage.Values[2]);
    if (Length(ProjectId) = 0) or (Length(DatabaseUrl) = 0) then
    begin
      MsgBox('Project ID and Database URL are required.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if Pos('https://', LowerCase(DatabaseUrl)) <> 1 then
    begin
      MsgBox('Database URL must start with https://', mbError, MB_OK);
      Result := False;
      Exit;
    end;
    // Regional RTDB instances use "<projectId>-default-rtdb.<region>...", so
    // accept both "." and "-" right after the project id.
    if (Pos(ProjectId + '.', DatabaseUrl) = 0) and (Pos(ProjectId + '-', DatabaseUrl) = 0) then
    begin
      MsgBox(Format('The Database URL does not belong to project "%s". Its host must start with the project id (e.g. https://%s-default-rtdb...).', [ProjectId, ProjectId]), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CaptureOldUninstallerDir;
var
  ArpKey, Value: string;
begin
  // Read the PREVIOUS install's uninstaller location before Inno rewrites the
  // ARP registration — used at ssPostInstall to delete the old hidden folder.
  ArpKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
    Chr(123) + '7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34' + Chr(125) + '_is1';
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, ArpKey, 'UninstallString', Value) then
  begin
    Value := Trim(Value);
    if (Length(Value) >= 2) and (Value[1] = '"') then
      Value := Copy(Value, 2, Length(Value) - 2);
    OldUninstDir := ExtractFileDir(Value);
  end;
end;

function ServiceExists(const Name: string): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'query ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

function ProcessRunning(const Image: string): Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\tasklist.exe'), '/FI "IMAGENAME eq ' + Image + '" /FO CSV /NH', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := ResultCode = 0;
end;

procedure WaitForServiceGone;
var
  i: Integer;
begin
  // Handle-exit polling: proceed as soon as the service record disappears (up to 15s).
  for i := 1 to 30 do
  begin
    if not ServiceExists('{#ServiceName}') then Exit;
    Sleep(500);
  end;
end;

procedure WaitForProcessesGone;
var
  i: Integer;
begin
  // Handle-exit polling: proceed once both agent processes exit (up to 15s).
  for i := 1 to 30 do
  begin
    if not (ProcessRunning('GuardPulse.Agent.Session.exe') or ProcessRunning('GuardPulse.Agent.Service.exe')) then Exit;
    Sleep(500);
  end;
end;

procedure StopExistingStack;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone;
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Session.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForProcessesGone;
end;

procedure RemoveStaleInstall;
var
  StaleDir: string;
begin
  // Stale install trees from a DIFFERENT directory than {app}: older installers
  // resolved {autopf} to the x86 Program Files, newer ones (Inno >= 6.3) to the
  // native one. An orphaned tree keeps runnable exes plus an old (possibly
  // poisoned) agent-config.json — exactly the split that produced a silently
  // dead service. Probe both Program Files variants and delete whichever does
  // not match {app}.
  StaleDir := ExpandConstant('{commonpf}\Device Service');
  if (UpperCase(StaleDir) <> UpperCase(ExpandConstant('{app}'))) and DirExists(StaleDir) then
    DelTree(StaleDir, True, True, True);
  StaleDir := ExpandConstant('{commonpf64}\Device Service');
  if (UpperCase(StaleDir) <> UpperCase(ExpandConstant('{app}'))) and DirExists(StaleDir) then
    DelTree(StaleDir, True, True, True);

  // Pre-0.2.1 installs were started at logon by an HKCU Run entry; remove it
  // (a duplicate session hits the single-instance mutex and crashes at logon).
  RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'DeviceServiceAgent');
end;

procedure GenerateAgentConfig;
var
  TemplatePath, DstPath: string;
  RawContent: AnsiString;
  Content: string;
begin
  DstPath := ExpandConstant('{app}\agent-config.json');
  TemplatePath := ExpandConstant('{app}\agent-config.template.json');

  if LoadStringFromFile(TemplatePath, RawContent) then
  begin
    Content := string(RawContent);
    StringChangeEx(Content, '__API_KEY__', Trim(FirebasePage.Values[0]), True);
    StringChangeEx(Content, '__PROJECT_ID__', Trim(FirebasePage.Values[1]), True);
    StringChangeEx(Content, '__DATABASE_URL__', Trim(FirebasePage.Values[2]), True);
    SaveStringToFile(DstPath, Content, False);
  end
  else
  begin
    SaveStringToFile(DstPath, Format('{"apiKey":"%s","projectId":"%s","databaseUrl":"%s","logLevel":"warning"}', [Trim(FirebasePage.Values[0]), Trim(FirebasePage.Values[1]), Trim(FirebasePage.Values[2])]), False);
  end;
end;

procedure LockLedgerPattern(const StateRoot, Icacls, Pattern: string);
var
  FindRec: TFindRec;
  Target: string;
  ResultCode: Integer;
begin
  // One file per icacls call: the multi-file form fails with error 87.
  if FindFirst(StateRoot + '\' + Pattern, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0 then
        begin
          Target := StateRoot + '\' + FindRec.Name;
          Exec(Icacls, Format('"%s" /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)"', [Target]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure ApplyStateDirectoryAcls;
var
  StateRoot: string;
  Icacls: string;
  ResultCode: Integer;
begin
  Icacls := ExpandConstant('{sys}\icacls.exe');
  StateRoot := ExpandConstant('{commonappdata}\GuardPulse\Laptop');

  Exec(Icacls, Format('"%s" /remove:g *S-1-5-32-545', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(Icacls, Format('"%s" /grant "*S-1-5-32-545:(RX)"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if FileExists(StateRoot + '\device.json') then
    Exec(Icacls, Format('"%s\device.json" /grant "*S-1-5-32-545:R"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  // Lock ledger files SYSTEM/Admin only (one file per call - multi-file fails err87)
  if FileExists(StateRoot + '\secrets.bin') then
    Exec(Icacls, Format('"%s\secrets.bin" /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // The DPAPI machine-scope mirror carries the same plaintext as secrets.bin
  // and is decryptable by any local account — lock it identically.
  if FileExists(StateRoot + '\secrets.bin.mirror') then
    Exec(Icacls, Format('"%s\secrets.bin.mirror" /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if FileExists(StateRoot + '\enforcement-state.json') then
    Exec(Icacls, Format('"%s\enforcement-state.json" /inheritance:r /grant:r "*S-1-5-18:(F)" "*S-1-5-32-544:(F)"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  LockLedgerPattern(StateRoot, Icacls, 'usage-*.json');
  LockLedgerPattern(StateRoot, Icacls, 'offsets-*.json');
  LockLedgerPattern(StateRoot, Icacls, 'blocks-*.json');

  // Lock logs dir
  Exec(Icacls, Format('"%s\logs" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)"', [StateRoot]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CleanupStaleDashboardArtifacts;
var
  ResultCode: Integer;
  Url: string;
begin
  // The local web dashboard was removed in 0.2.13. Older installs reserved this
  // URL ACL and dropped browser shortcuts for it: delete the stale reservation
  // (result ignored — absent is the desired state) and remove leftover .url files.
  Url := 'http://127.0.0.1:37841/';
  Exec(ExpandConstant('{sys}\netsh.exe'), Format('http delete urlacl url=%s', [Url]), '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ExpandConstant('{commondesktop}\GuardPulse Dashboard.url'));
  DeleteFile(ExpandConstant('{commonprograms}\GuardPulse\Dashboard.url'));
end;

procedure LockUninstallerDir(const Dir: string);
var
  Icacls, Cmd: string;
  ResultCode: Integer;
begin
  // SYSTEM/Admins only + hidden: a standard user with "show hidden items" ON
  // can neither list this folder nor run the uninstaller inside it. (The ARP
  // key stays world-readable on purpose — it is the parent's entry point, and
  // it is PIN-gated anyway.)
  Icacls := ExpandConstant('{sys}\icacls.exe');
  Cmd := Format('"%s" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)(F)" "*S-1-5-32-544:(OI)(CI)(F)"', [Dir]);
  Exec(Icacls, Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\attrib.exe'), '+h "' + Dir + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function RegisterSentinelTask: Boolean;
var
  ResultCode: Integer;
  PsCmd: string;
begin
  // schtasks from XML would need a temp file; PowerShell one-liners keep it
  // self-contained. The task runs sentinel.ps1 as SYSTEM (highest, hidden):
  // at boot, at logon, and every 30 minutes.
  PsCmd :=
    '$action = New-ScheduledTaskAction -Execute powershell.exe -Argument ''-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\Windows\System32\GuardPulse\sentinel.ps1''; ' +
    '$t1 = New-ScheduledTaskTrigger -AtStartup; ' +
    '$t2 = New-ScheduledTaskTrigger -AtLogOn; ' +
    '$t3 = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(30) -RepetitionInterval (New-TimeSpan -Minutes 30) -RepetitionDuration (New-TimeSpan -Days 3650); ' +
    '$p = New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest; ' +
    '$s = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit (New-TimeSpan -Minutes 10); ' +
    'Register-ScheduledTask -TaskName GuardPulseSentinel -Action $action -Trigger @($t1,$t2,$t3) -Principal $p -Settings $s -Force | Out-Null';
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -ExecutionPolicy Bypass -Command "' + PsCmd + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
  if Result then
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/run /tn GuardPulseSentinel', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure HideUninstaller;
var
  UninsExe, UninsDat, UninsMsg, NewDir, NewExe, NewDat, NewMsg, ArpKey, Chars: string;
  i: Integer;
begin
  // Move the Inno uninstaller (unins###.exe/.dat/.msg — Inno 6.3+ keeps the
  // uninstaller's UI strings in a .msg sidecar) into a RANDOM per-install
  // ProgramData folder with a bland name. The path is generated here from the
  // strong RNG and recorded only in the hidden ARP registry entry — nothing
  // GuardPulse-related marks where it lives.
  Chars := 'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
  NewDir := ExpandConstant('{commonappdata}') + '\';
  for i := 1 to 12 do
    NewDir := NewDir + Chars[Random(Length(Chars)) + 1];
  ForceDirectories(NewDir);

  NewExe := NewDir + '\devdiag.exe';
  NewDat := NewDir + '\devdiag.dat';
  NewMsg := NewDir + '\devdiag.msg';

  UninsExe := ExpandConstant('{uninstallexe}');
  UninsDat := Copy(UninsExe, 1, Length(UninsExe) - Length(ExtractFileExt(UninsExe))) + '.dat';
  UninsMsg := Copy(UninsExe, 1, Length(UninsExe) - Length(ExtractFileExt(UninsExe))) + '.msg';

  if not FileExists(UninsExe) then
    Exit;

  if FileExists(NewExe) then DeleteFile(NewExe);
  if FileExists(NewDat) then DeleteFile(NewDat);
  if FileExists(NewMsg) then DeleteFile(NewMsg);

  if not RenameFile(UninsExe, NewExe) then
    Exit;

  // Roll back if the .dat did not follow: never leave an exe without its data
  if not RenameFile(UninsDat, NewDat) then
  begin
    RenameFile(NewExe, UninsExe);
    Exit;
  end;

  // The .msg sidecar must move too: Inno 6.3+ refuses to start the uninstaller
  // without its Messages file ("Messages file ... is missing" — it dies before
  // InitializeUninstall, so the PIN gate never runs). Older Inno emits no
  // .msg — skip the rename when absent.
  if FileExists(UninsMsg) then
  begin
    if not RenameFile(UninsMsg, NewMsg) then
    begin
      RenameFile(NewDat, UninsDat);
      RenameFile(NewExe, UninsExe);
      Exit;
    end;
  end;

  // Track the hidden exe path for version-resource stripping after install.
  HiddenUninstExe := NewExe;

  // SYSTEM/Admins-only + hidden: a standard user cannot see or run the
  // uninstaller even with "show hidden items" enabled.
  LockUninstallerDir(NewDir);

  // Keep Add/Remove Programs functional but pointed at the hidden location.
  // The GUID braces are built via Chr() to sidestep preprocessor escaping.
  ArpKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
    Chr(123) + '7C4E8091-A3B2-4D5F-8E6A-C5D2F0A91B34' + Chr(125) + '_is1';
  RegWriteStringValue(HKEY_LOCAL_MACHINE, ArpKey, 'UninstallString', '"' + NewExe + '"');
  RegWriteStringValue(HKEY_LOCAL_MACHINE, ArpKey, 'QuietUninstallString', '"' + NewExe + '" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART');
  // Hide "Device Service" from Control Panel / Settings > Apps. The parent
  // uninstalls by running the hidden exe above as administrator.
  RegWriteDWordValue(HKEY_LOCAL_MACHINE, ArpKey, 'SystemComponent', 1);

  // Remove the OLD uninstaller folder (previous random dir, or the pre-0.2.5
  // GuardPulse\Laptop\sys / {app} locations). Never touch the live app folder.
  if (OldUninstDir <> '') and (LowerCase(OldUninstDir) <> LowerCase(NewDir)) and
     (Pos('device service', LowerCase(OldUninstDir)) = 0) then
    DelTree(OldUninstDir, True, True, True);
  DelTree(ExpandConstant('{commonappdata}\GuardPulse\Laptop\sys'), True, True, True);

  // Remove legacy unins files from the pre-0.2.5 {app} location (upgrade installs)
  DeleteFile(ExpandConstant('{app}\unins000.exe'));
  DeleteFile(ExpandConstant('{app}\unins000.dat'));
  DeleteFile(ExpandConstant('{app}\unins000.msg'));
end;

procedure StripUninstallerMetadata;
var
  Ps1Path, Ps1: string;
  ResultCode: Integer;
begin
  // Inno stamps the uninstaller with FileDescription "Setup/Uninstall",
  // FileVersion "51.1054.0.0" and an Inno Setup comment, all inside the
  // RT_VERSION resource — no directive can change those. Delete the whole
  // resource so Explorer shows no metadata at all. Best-effort: if PowerShell
  // is unavailable or the call fails, the exe simply keeps Inno's neutral
  // strings (no GuardPulse text either way). Never fails the install.
  if HiddenUninstExe = '' then
    Exit; // HideUninstaller did not complete; nothing to strip

  try
    Ps1Path := ExpandConstant('{tmp}\stripver.ps1');
    Ps1 := 'param([string]$Path)' + #13#10 +
      '$src = @"' + #13#10 +
      'using System;' + #13#10 +
      'using System.Runtime.InteropServices;' + #13#10 +
      'public static class VerRes {' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]' + #13#10 +
      '  public static extern IntPtr BeginUpdateResourceW(string pFileName, bool bDeleteExistingResources);' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]' + #13#10 +
      '  public static extern bool UpdateResourceW(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, IntPtr lpData, uint cb);' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true)]' + #13#10 +
      '  public static extern bool EndUpdateResourceW(IntPtr hUpdate, bool fDiscard);' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]' + #13#10 +
      '  public static extern IntPtr LoadLibraryExW(string lpFileName, IntPtr hFile, uint dwFlags);' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true)]' + #13#10 +
      '  public static extern bool FreeLibrary(IntPtr hModule);' + #13#10 +
      '  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]' + #13#10 +
      '  public static extern bool EnumResourceLanguagesW(IntPtr hModule, IntPtr lpType, IntPtr lpName, EnumResLangProc lpEnumFunc, IntPtr lParam);' + #13#10 +
      '  public delegate bool EnumResLangProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, ushort wIDLanguage, IntPtr lParam);' + #13#10 +
      '}' + #13#10 +
      '"@' + #13#10 +
      'Add-Type -TypeDefinition $src' + #13#10 +
      'if (-not (Test-Path -LiteralPath $Path)) { exit 2 }' + #13#10 +
      '$langs = New-Object System.Collections.Generic.List[int16]' + #13#10 +
      '$cb = [VerRes+EnumResLangProc]{ param($m,$t,$n,$l,$p) $langs.Add($l); return $true }' + #13#10 +
      '$hMod = [VerRes]::LoadLibraryExW($Path, [IntPtr]::Zero, 2)' + #13#10 +
      'if ($hMod -ne [IntPtr]::Zero) {' + #13#10 +
      '  $null = [VerRes]::EnumResourceLanguagesW($hMod, [IntPtr]16, [IntPtr]1, $cb, [IntPtr]::Zero)' + #13#10 +
      '  [VerRes]::FreeLibrary($hMod) | Out-Null' + #13#10 +
      '}' + #13#10 +
      '$h = [VerRes]::BeginUpdateResourceW($Path, $false)' + #13#10 +
      'if ($h -eq [IntPtr]::Zero) { exit 3 }' + #13#10 +
      'if ($langs.Count -eq 0) { $langs.Add([int16]0x0409) }' + #13#10 +
      'foreach ($l in $langs) {' + #13#10 +
      '  $null = [VerRes]::UpdateResourceW($h, [IntPtr]16, [IntPtr]1, $l, [IntPtr]::Zero, [uint32]0)' + #13#10 +
      '}' + #13#10 +
      '$ok = [VerRes]::EndUpdateResourceW($h, $false)' + #13#10 +
      'if ($ok) { exit 0 } else { exit 4 }' + #13#10;

    if SaveStringToFile(Ps1Path, Ps1, False) then
    begin
      Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + Ps1Path + '" "' + HiddenUninstExe + '"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      DeleteFile(Ps1Path);
    end;
  except
    // Polish only; ignore any failure.
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  case CurStep of
    ssInstall:
    begin
      StopExistingStack;
      RemoveStaleInstall;
      CaptureOldUninstallerDir;
    end;

    ssPostInstall:
    begin
      GenerateAgentConfig;
      ApplyStateDirectoryAcls;
      CleanupStaleDashboardArtifacts;

      // Configure service via sc.exe
      Exec(ExpandConstant('{sys}\sc.exe'),
        Format('create {#ServiceName} binPath= "\"%s\"" start= auto DisplayName= "%s"', [ExpandConstant('{app}\GuardPulse.Agent.Service.exe'), '{#AppName}']),
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
      begin
        MsgBox(Format('Failed to create the {#ServiceName} service (error %d).', [ResultCode]), mbError, MB_OK);
        RaiseException('Service creation failed.');
      end;

      Exec(ExpandConstant('{sys}\sc.exe'),
        'description {#ServiceName} "Device background service."',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'),
        'failure {#ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Exec(ExpandConstant('{sys}\sc.exe'),
        'sdset {#ServiceName} "D:(A;;GA;;;SY)(A;;GA;;;BA)"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      // Start the service. A first attempt can fail transiently (observed:
      // error 2 right after sc create on 0.2.36's 09-10 install) while SCM is
      // still settling — wait once and retry before surfacing an error. The
      // sentinel below also starts the service as SYSTEM on its first run.
      Exec(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
      begin
        Sleep(3000);
        Exec(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      end;
      if ResultCode <> 0 then
        MsgBox(Format('The {#ServiceName} service could not be started (error %d). It will start automatically at the next boot.', [ResultCode]), mbError, MB_OK);

      // Self-repair sentinel: hidden SYSTEM task (boot + logon + every 30 min)
      // that re-arms a stopped/disabled/deleted service, restores startup entries
      // and browser blocks, and records every intervention for tamper events.
      SentinelDeployed := RegisterSentinelTask;

      // Last: hide the uninstaller (files were written before this step),
      // then strip its version resource so it shows no metadata in Explorer.
      HideUninstaller;
      StripUninstallerMetadata;

      // Re-apply the hidden attribute on the app folder (belt-and-braces for
      // upgrades, where Inno's [Dirs] attribute is not reapplied if the folder
      // already exists). Result ignored: non-fatal if it ever fails.
      Exec(ExpandConstant('{sys}\attrib.exe'), '+h "' + ExpandConstant('{app}') + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;

procedure StopAgentStack;
var
  ResultCode: Integer;
  i: Integer;
begin
  // Order matters: stopping the service disposes the watchdog, otherwise killing
  // the session below would get it respawned within ~10s. Then kill the session
  // (a standalone per-logon process that file-locks its own exe/dlls even after
  // the service dies), then the service, then delete the service. Result codes
  // are ignored so an already-stopped/already-gone process never aborts uninstall.
  // Handle-exit polling (up to 15s per stage) replaces the old fixed Sleeps.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Session.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForProcessesGone;
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  WaitForServiceGone;
end;

// ---------------------------------------------------------------------------
// Uninstall PIN gate: removal requires the parent PIN (the same one set from
// the phone app). The gate reads uninstall-pin.json (SYSTEM/Admins-only mirror
// of the current PIN, written by the service), verifies via PBKDF2 (v2) or
// SHA-256 (legacy v1), and on success writes uninstall-permit.json (15-minute
// TTL) which usUninstall consumes. Wrong attempts append to
// uninstall-attempt.json; the running service reports them to the phone as
// tamper events and the escalating rejection window stops guess spam.
// ---------------------------------------------------------------------------

function PinFileExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\GuardPulse\Laptop\uninstall-pin.json'));
end;

function PermitFileExists: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonappdata}\GuardPulse\Laptop\uninstall-permit.json'));
end;

procedure ConsumePermit;
begin
  DeleteFile(ExpandConstant('{commonappdata}\GuardPulse\Laptop\uninstall-permit.json'));
end;

procedure RemoveSentinel;
var
  ResultCode: Integer;
begin
  // Kill the sentinel BEFORE anything else so it cannot re-arm mid-uninstall.
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/delete /tn GuardPulseSentinel /f',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DelTree(ExpandConstant('{sys}\GuardPulse'), True, True, True);
end;

// Prompts for the 6-digit PIN. Inno's uninstaller has no InputQuery, so this
// shells a tiny WinForms input box through PowerShell (elevated already).
// Returns False when cancelled. Trims to digits-only, max 6 chars.
function PromptForPin(var Pin: string): Boolean;
var
  ResultCode: Integer;
  Script, OutFile, PsCmd, ScriptPath: string;
  Raw: AnsiString;
begin
  Result := False;
  Pin := '';
  OutFile := ExpandConstant('{tmp}') + '\pin-answer.txt';
  DeleteFile(OutFile);
  Script :=
    'Add-Type -AssemblyName Microsoft.VisualBasic' + #13#10 +
    '$answer = [Microsoft.VisualBasic.Interaction]::InputBox(' + Chr(39) + 'Enter the 6-digit parent PIN to allow removal:' + Chr(39) + ', ' + Chr(39) + 'GuardPulse' + Chr(39) + ', ' + Chr(39) + Chr(39) + ')' + #13#10 +
    'Set-Content -Path ' + Chr(39) + '%OUT%' + Chr(39) + ' -Value $answer -Force';
  StringChangeEx(Script, '%OUT%', OutFile, True);
  ScriptPath := ExpandConstant('{tmp}') + '\pin-prompt.ps1';
  SaveStringToFile(ScriptPath, Script, False);
  PsCmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '"';
  Exec(ExpandConstant('{sys}') + '\WindowsPowerShell\v1.0\powershell.exe',
    PsCmd, '', SW_SHOW, ewWaitUntilTerminated, ResultCode);
  DeleteFile(ScriptPath);
  if not FileExists(OutFile) then
    Exit; // cancelled / window closed
  LoadStringFromFile(OutFile, Raw);
  DeleteFile(OutFile);
  Pin := Trim(string(Raw));
  // Cancelled (empty answer) counts as cancel.
  if Pin = '' then
    Exit;
  Result := True;
end;

// Runs the elevated PowerShell verify helper: verifies Pin against
// uninstall-pin.json; on success writes uninstall-permit.json; on failure
// records uninstall-attempt.json for tamper reporting. Exit code: 0 granted,
// 2 wrong PIN, 4 pin file missing (gate open -> allowed).
function TryVerifyPin(const Pin: string): Boolean;
var
  HelperScript, PsCmd: string;
  ResultCode: Integer;
  Q: Char;
begin
  Q := Chr(39); // single quote
  HelperScript := ExpandConstant('{tmp}') + '\verify-pin.ps1';
  SaveStringToFile(HelperScript,
    'param([string]$Pin)' + #13#10 +
    '$ErrorActionPreference = ' + Q + 'Stop' + Q + #13#10 +
    '$state = Join-Path $env:ProgramData ' + Q + 'GuardPulse\Laptop' + Q + #13#10 +
    '$pinFile = Join-Path $state ' + Q + 'uninstall-pin.json' + Q + #13#10 +
    'if (-not (Test-Path $pinFile)) { exit 4 }' + #13#10 +
    '$pin = Get-Content $pinFile -Raw | ConvertFrom-Json' + #13#10 +
    'function B64Url([string]$s) { $p = $s.Replace(' + Q + '-' + Q + ',' + Q + '+' + Q + ').Replace(' + Q + '_' + Q + ',' + Q + '/' + Q + '); switch ($p.Length % 4) { 2 { $p = $p + ' + Q + '==' + Q + ' } 3 { $p = $p + ' + Q + '=' + Q + ' } } [Convert]::FromBase64String($p) }' + #13#10 +
    '$ok = $false' + #13#10 +
    'try {' + #13#10 +
    '  if ($pin.Version -eq 2) {' + #13#10 +
    '    $salt = B64Url $pin.Salt' + #13#10 +
    '    $d = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($Pin, $salt, [int]$pin.Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)' + #13#10 +
    '    $key = $d.GetBytes(32)' + #13#10 +
    '    $actual = [Convert]::ToBase64String($key).TrimEnd(' + Q + '=' + Q + ').Replace(' + Q + '+' + Q + ',' + Q + '-' + Q + ').Replace(' + Q + '/' + Q + ',' + Q + '_' + Q + ')' + #13#10 +
    '    $ok = ($actual -eq $pin.Hash)' + #13#10 +
    '  } else {' + #13#10 +
    '    $sha = [System.Security.Cryptography.SHA256]::Create()' + #13#10 +
    '    $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($pin.Salt + ' + Q + ':' + Q + ' + $Pin)))' + #13#10 +
    '    $actual = [Convert]::ToBase64String($bytes).TrimEnd(' + Q + '=' + Q + ').Replace(' + Q + '+' + Q + ',' + Q + '-' + Q + ').Replace(' + Q + '/' + Q + ',' + Q + '_' + Q + ')' + #13#10 +
    '    $ok = ($actual -eq $pin.Hash)' + #13#10 +
    '  }' + #13#10 +
    '} catch { $ok = $false }' + #13#10 +
    'if (-not $ok) {' + #13#10 +
    '  $attFile = Join-Path $state ' + Q + 'uninstall-attempt.json' + Q + #13#10 +
    '  $count = 1; $latest = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); $prev = $null' + #13#10 +
    '  try { $prev = Get-Content $attFile -Raw | ConvertFrom-Json } catch { }' + #13#10 +
    '  if ($prev -and $prev.count) { $count = [int]$prev.count + 1 }' + #13#10 +
    '  if ($prev -and $prev.latestAtMs) { $latest = [int64]$prev.latestAtMs }' + #13#10 +
    '  $payload = @{ count = $count; latestAtMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() } | ConvertTo-Json -Compress' + #13#10 +
    '  Set-Content -Path $attFile -Value $payload -Force' + #13#10 +
    '  icacls $attFile /grant:r *S-1-5-18:F *S-1-5-32-544:F | Out-Null' + #13#10 +
    '  exit 2' + #13#10 +
    '}' + #13#10 +
    '$permitPath = Join-Path $state ' + Q + 'uninstall-permit.json' + Q + #13#10 +
    '$permit = @{ issuedAtMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); nonce = [Guid]::NewGuid().ToString(' + Q + 'N' + Q + ') } | ConvertTo-Json -Compress' + #13#10 +
    'Set-Content -Path $permitPath -Value $permit -Force' + #13#10 +
    'icacls $permitPath /inheritance:r /grant:r *S-1-5-18:F *S-1-5-32-544:F | Out-Null' + #13#10 +
    'exit 0', False);
  PsCmd := '-NoProfile -ExecutionPolicy Bypass -File "' + HelperScript + '" -Pin "' + Pin + '"';
  Exec(ExpandConstant('{sys}') + '\WindowsPowerShell\v1.0\powershell.exe',
    PsCmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  DeleteFile(HelperScript);
  Result := (ResultCode = 0);
end;

function InitializeUninstall(): Boolean;
var
  Pin: string;
  Attempts: Integer;
begin
  Result := True;

  // Gate open (never-paired device / parent cleared the PIN): uninstall freely.
  if not PinFileExists then
    Exit;

  // A pre-staged permit (or a retry after a failed teardown) passes through.
  if PermitFileExists then
  begin
    RemoveSentinel;
    ConsumePermit;
    Exit;
  end;

  if UninstallSilent then
  begin
    MsgBox('Removal requires the parent PIN. Run this uninstaller normally (not silently) and enter the parent PIN.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Attempts := 0;
  while True do
  begin
    if not PromptForPin(Pin) then
    begin
      Result := False;
      Exit;
    end;

    if (Length(Pin) = 6) and TryVerifyPin(Pin) then
      Break;

    // Gate might have opened meanwhile (parent cleared the PIN).
    if not PinFileExists then
      Exit;

    Attempts := Attempts + 1;
    if Attempts >= 5 then
    begin
      MsgBox('Too many wrong PIN attempts. Uninstall cancelled. This attempt was reported to the parent''s phone.', mbError, MB_OK);
      Result := False;
      Exit;
    end;

    MsgBox('Wrong PIN. Try again (' + IntToStr(5 - Attempts) + ' attempts left).', mbError, MB_OK);
  end;

  // Verified: dismantle the sentinel first, then consume the permit the helper
  // wrote (usUninstall proceeds without further checks).
  RemoveSentinel;
  ConsumePermit;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);

var
  ResultCode: Integer;
begin
  case CurUninstallStep of
    usUninstall:
    begin
      StopAgentStack;

      // Optional data wipe: secrets, usage ledger, activity log and pairing state
      // under %ProgramData% are otherwise left behind after uninstall.
      // Interactive only: a silent uninstall (/VERYSILENT) must NEVER auto-wipe
      // pairing data, and when prompted the default is No (MB_DEFBUTTON2).
      if (not UninstallSilent) and
         (MsgBox('Remove all device data (secrets, usage history, activity log, pairing state)?', mbConfirmation, MB_YESNO + MB_DEFBUTTON2) = IDYES) then
        DelTree(ExpandConstant('{commonappdata}\GuardPulse\Laptop'), True, True, True);
    end;
    usPostUninstall:
    begin
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\{#ServiceName}');
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SYSTEM\CurrentControlSet\Control\SafeBoot\Network\{#ServiceName}');

      // Legacy install.ps1-era entries that the installer never wrote.
      RegDeleteValue(HKEY_CURRENT_USER,
        'Software\Microsoft\Windows\CurrentVersion\Run', 'DeviceServiceAgent');
      // The 32-bit Inno uninstaller sees the WOW6432Node view of HKLM, so its own
      // [Registry] uninsdeletevalue only cleans that view. The legacy install.ps1
      // value lives in the native 64-bit view — delete it with the native reg.exe
      // (from {sysnative} which points to the 64-bit System32 when called from a
      // 32-bit process). The WOW6432Node view is cleaned by this uninstaller's
      // own uninsdeletevalue flag; a 64-bit-innocent belt-and-braces delete of
      // the native view covers installs made by the old install.ps1.
      Exec(ExpandConstant('{sysnative}\reg.exe'),
        'delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v DeviceServiceAgent /f',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      // Dashboard URL ACL reservation; result ignored (already gone is fine).
      Exec(ExpandConstant('{sys}\netsh.exe'),
        'http delete urlacl url=http://127.0.0.1:37841/',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      // Leftover hidden-uninstaller folder (its exe is pending self-delete;
      // DelTree silently skips anything still locked).
      DelTree(ExpandConstant('{commonappdata}\GuardPulse\Laptop\sys'), True, True, True);

      // Browser policy keys the agent writes while running (URLBlocklist +
      // forced DoH-off). Without this the browsers stay blocked forever after
      // uninstall, with no agent left to lift the block. Only owned values and
      // subkeys are removed — the parent policy keys may hold unrelated
      // admin-tooling entries.
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Google\Chrome\URLBlocklist');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Google\Chrome', 'DnsOverHttpsMode');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Google\Chrome', 'DnsOverHttpsTemplates');
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Microsoft\Edge\URLBlocklist');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Microsoft\Edge', 'DnsOverHttpsMode');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Microsoft\Edge', 'DnsOverHttpsTemplates');
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\BraveSoftware\Brave\URLBlocklist');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\BraveSoftware\Brave', 'DnsOverHttpsMode');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\BraveSoftware\Brave', 'DnsOverHttpsTemplates');
      RegDeleteKeyIncludingSubkeys(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Mozilla\Firefox\WebsiteFilter');
      RegDeleteValue(HKEY_LOCAL_MACHINE,
        'SOFTWARE\Policies\Mozilla\Firefox', 'DNSOverHTTPS');

      // Hosts-file GuardPulse content-filter block: strip the marked section so
      // blocked sites resolve again (Inno has no in-place file editing, so the
      // rewrite runs through PowerShell). Result ignored: no block = nothing to do.
      Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
        '-NoProfile -Command "$p=''C:\Windows\System32\drivers\etc\hosts''; $c=Get-Content $p -Raw -ErrorAction SilentlyContinue; $m=''# END GUARDPULSE CONTENT FILTER''; $b=$c.IndexOf(''# BEGIN GUARDPULSE''); $e=$c.IndexOf($m); if(($null -ne $c) -and ($b -ge 0) -and ($e -ge 0)){Set-Content -Path $p -Value $c.Remove($b,($e+$m.Length)-$b) -Encoding ASCII -Force; ipconfig /flushdns | Out-Null}"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
