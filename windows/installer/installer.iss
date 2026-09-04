; GuardPulse Laptop — Inno Setup installer
; Replaces install.ps1: bundles the merged ReadyToRun payload, collects Firebase
; credentials in the wizard, and configures the service, Safe Boot keys, ACLs,
; and Run-key fallback — all elevated, all silent.

#define AppVersion "0.2.18"
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
VersionInfoVersion=0.2.13

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "agent-config.template.json"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
; Removes runtime-created files (agent-config.json, local logs) on uninstall.
Type: filesandordirs; Name: "{app}"
; Removes the stale pre-0.2.1 install that lived under the 64-bit Program Files
; (an early install.ps1-era path). Its HKCU Run entry is deleted in code below;
; leaving this folder behind let a duplicate session start at every logon and
; crash on the single-instance mutex.
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
  FirebasePage.Values[1] := 'guardpulse-laptop-control';
  FirebasePage.Values[2] := 'https://guardpulse-laptop-control-default-rtdb.firebaseio.com';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = FirebasePage.ID then
  begin
    if Length(Trim(FirebasePage.Values[0])) = 0 then
    begin
      MsgBox('API Key is required.', mbError, MB_OK);
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

procedure StopExistingStack;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Session.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
end;

procedure RemoveStaleInstall;
var
  StaleDir: string;
begin
  // Pre-0.2.1 installs lived under the 64-bit Program Files (install.ps1-era) and
  // were started at logon by an HKCU Run entry. That duplicate session hits the
  // single-instance mutex and crashes on every logon; remove both on (re)install.
  StaleDir := ExpandConstant('{commonpf64}\Device Service');
  if DirExists(StaleDir) then
    DelTree(StaleDir, True, True, True);
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

procedure HideUninstaller;
var
  UninsExe, UninsDat, NewDir, NewExe, NewDat, ArpKey, Chars: string;
  i: Integer;
begin
  // Move the Inno uninstaller (unins###.exe/.dat) into a RANDOM per-install
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

  UninsExe := ExpandConstant('{uninstallexe}');
  UninsDat := Copy(UninsExe, 1, Length(UninsExe) - Length(ExtractFileExt(UninsExe))) + '.dat';

  if not FileExists(UninsExe) then
    Exit;

  if FileExists(NewExe) then DeleteFile(NewExe);
  if FileExists(NewDat) then DeleteFile(NewDat);

  if not RenameFile(UninsExe, NewExe) then
    Exit;

  // Roll back if the .dat did not follow: never leave an exe without its data
  if not RenameFile(UninsDat, NewDat) then
  begin
    RenameFile(NewExe, UninsExe);
    Exit;
  end;

  // Track the hidden exe path for version-resource stripping after install.
  HiddenUninstExe := NewExe;

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

      // Start the service
      Exec(ExpandConstant('{sys}\net.exe'), 'start {#ServiceName}',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ResultCode <> 0 then
        MsgBox(Format('The {#ServiceName} service could not be started (error %d). It will start automatically at the next boot.', [ResultCode]), mbError, MB_OK);

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
begin
  // Order matters: stopping the service disposes the watchdog, otherwise killing
  // the session below would get it respawned within ~10s. Then kill the session
  // (a standalone per-logon process that file-locks its own exe/dlls even after
  // the service dies), then the service, then delete the service. Result codes
  // are ignored so an already-stopped/already-gone process never aborts uninstall.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(2000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Session.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM GuardPulse.Agent.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
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
      // 32-bit process).
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
    end;
  end;
end;
