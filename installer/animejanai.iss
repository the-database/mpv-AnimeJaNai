; AnimeJaNai setup installer (Inno Setup 6).
;
; Wraps the slim core tree the package builder produces (the same tree that
; ships as the portable .7z) into a per-user Windows installer. Built in CI by
; deploy.yml:
;   ISCC.exe installer\animejanai.iss /DAppVersion=<ver> /DSourceDir=<tree>
;
; PER-USER, NO ADMIN, by necessity: the app writes into its own folder at
; runtime (TensorRT engines build into animejanai\onnx, the updater/Manager
; extract component packs and self-update in place, mpv.net writes its config
; under portable_config). A Program Files install would break those writes for
; a standard user, so the default location is %LOCALAPPDATA%\Programs.
;
; Components (TensorRT runtime + the GPU's kernels, RIFE models) are NOT
; bundled - they download post-install via AnimeJaNaiUpdater.exe, GPU-matched,
; keeping the installer small (~150 MB). A failed download is non-fatal: the
; player still runs (DirectML is in the core; NVIDIA falls back to unfiltered)
; and the Manager's first-run dialog re-offers the same install.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error Define SourceDir (the built slim tree) with /DSourceDir=...
#endif

; The player application is "mpv-AnimeJaNai" (AnimeJaNai alone is the upscaling
; model family; this is the mpv-based player that runs it).
#define AppName "mpv-AnimeJaNai"
#define Publisher "the-database"
#define PlayerExe "mpvnet.exe"
#define ManagerExe "AnimeJaNaiManager.exe"
#define UpdaterExe "AnimeJaNaiUpdater.exe"

[Setup]
AppId={{8B2F4E1A-9C3D-4A7E-B5F6-AJANAI340MPV}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL=https://github.com/the-database/mpv-upscale-2x_animejanai
WizardStyle=modern
; Per-user install: no elevation, lands in %LOCALAPPDATA%\Programs\AnimeJaNai,
; directory still changeable on the standard page.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#PlayerExe}
UninstallDisplayName={#AppName} {#AppVersion}
OutputBaseFilename=mpv-AnimeJaNai-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "assocvideo"; Description: "Associate common &video file types with {#AppName}"

[Files]
; The updater needs its own entry (not just the wildcard) so ExtractTemporaryFile
; can run it for GPU detection during the wizard; exclude it from the wildcard to
; avoid listing it twice.
Source: "{#SourceDir}\{#UpdaterExe}"; DestDir: "{app}"; Flags: ignoreversion

; User-owned files: write the shipped default only on a FRESH install; never clobber an
; existing user copy on a reinstall-over (mirrors the updater's manifest "user_preserve"
; deny-list in BuildMpvUpscale2xAnimeJaNai/Program.cs). Without onlyifdoesntexist the
; wildcard below would reset the user's profiles, keybindings, mpv settings, and mpv.net
; state (last-played video / window / volume) to the package defaults on every reinstall.
; Keep this list in sync with that manifest's user_preserve array.
Source: "{#SourceDir}\animejanai\animejanai.conf";       DestDir: "{app}\animejanai";       Flags: onlyifdoesntexist
Source: "{#SourceDir}\portable_config\mpv.conf";         DestDir: "{app}\portable_config";  Flags: onlyifdoesntexist
Source: "{#SourceDir}\portable_config\input.conf";       DestDir: "{app}\portable_config";  Flags: onlyifdoesntexist
Source: "{#SourceDir}\portable_config\saved-props.json"; DestDir: "{app}\portable_config";  Flags: onlyifdoesntexist
Source: "{#SourceDir}\portable_config\settings.xml";     DestDir: "{app}\portable_config";  Flags: onlyifdoesntexist

Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "{#UpdaterExe},animejanai\animejanai.conf,portable_config\mpv.conf,portable_config\input.conf,portable_config\saved-props.json,portable_config\settings.xml"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#PlayerExe}"
; The companion tool keeps its own name ("AnimeJaNai Manager" - it manages the
; AnimeJaNai models/components), matching its window title.
Name: "{group}\AnimeJaNai Manager"; Filename: "{app}\{#ManagerExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#PlayerExe}"; Tasks: desktopicon

; Per-user (HKCU) video file associations, gated on the assocvideo task. Registers
; an AnimeJaNai.mpv ProgID and adds it to each extension's Open-with list plus the
; per-user default. (Win10+ protects the *effective* default behind a UserChoice
; hash setup can't forge; this is the standard achievable registration - it makes
; AnimeJaNai available and the per-user fallback default.)
[Registry]
Root: HKCU; Subkey: "Software\Classes\AnimeJaNai.mpv"; ValueType: string; ValueName: ""; ValueData: "{#AppName} Video"; Flags: uninsdeletekey; Tasks: assocvideo
; FriendlyAppName is what the "Open with" / default-app picker shows, instead of
; the exe's own name ("mpvnet"). We don't rename the exe (it is mpv.net's binary).
Root: HKCU; Subkey: "Software\Classes\AnimeJaNai.mpv"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: assocvideo
Root: HKCU; Subkey: "Software\Classes\AnimeJaNai.mpv\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#PlayerExe},0"; Tasks: assocvideo
Root: HKCU; Subkey: "Software\Classes\AnimeJaNai.mpv\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#PlayerExe}"" ""%1"""; Tasks: assocvideo
Root: HKCU; Subkey: "Software\Classes\AnimeJaNai.mpv\shell\open"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Tasks: assocvideo
; One pair of entries per video extension: add the ProgID to the extension's
; Open-with list, and set it as the per-user default ProgID.
#define public Assoc(str Ext) \
  "Root: HKCU; Subkey: ""Software\Classes\" + Ext + "\OpenWithProgids""; ValueType: string; ValueName: ""AnimeJaNai.mpv""; ValueData: """"; Flags: uninsdeletevalue; Tasks: assocvideo" + NewLine + \
  "Root: HKCU; Subkey: ""Software\Classes\" + Ext + """; ValueType: string; ValueName: """"; ValueData: ""AnimeJaNai.mpv""; Flags: uninsdeletevalue; Tasks: assocvideo"
{#Assoc(".mkv")}
{#Assoc(".mp4")}
{#Assoc(".avi")}
{#Assoc(".mov")}
{#Assoc(".webm")}
{#Assoc(".m2ts")}
{#Assoc(".ts")}
{#Assoc(".wmv")}
{#Assoc(".flv")}
{#Assoc(".m4v")}
{#Assoc(".mpg")}
{#Assoc(".mpeg")}
{#Assoc(".ogv")}
{#Assoc(".3gp")}
{#Assoc(".mts")}
{#Assoc(".m2v")}

; Remove the whole per-user app folder on uninstall, including files created at
; runtime that the installer never tracked (built engines + timing caches in
; animejanai\onnx and animejanai\rife, downloaded component packs, components.json,
; logs, mpv.net state).
[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  CompPage: TInputOptionWizardPage;
  GpuName: String;
  HasNvidia: Boolean;
  TrtPacks: String;   // comma-separated, detected at wizard start (best-effort)
  RifePack: String;

procedure SHChangeNotify(wEventId: Integer; uFlags: Cardinal; dwItem1, dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';

// Run AnimeJaNaiUpdater.exe --recommend (from the given exe path), capturing its
// KEY=value lines into the globals. Returns False if it could not be run.
function RunRecommend(ExePath: String): Boolean;
var
  TmpFile, Cmd: String;
  Lines: TArrayOfString;
  i, eq: Integer;
  key, val: String;
  rc: Integer;
begin
  Result := False;
  TmpFile := ExpandConstant('{tmp}\aji-recommend.txt');
  Cmd := '/C ""' + ExePath + '" --recommend > "' + TmpFile + '" 2>&1"';
  if not Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Exit;
  if not LoadStringsFromFile(TmpFile, Lines) then
    Exit;
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    eq := Pos('=', Lines[i]);
    if eq > 0 then
    begin
      key := Copy(Lines[i], 1, eq - 1);
      val := Copy(Lines[i], eq + 1, Length(Lines[i]) - eq);
      if key = 'NVIDIA' then HasNvidia := (val = '1')
      else if key = 'GPU' then GpuName := val
      else if key = 'TRT_PACKS' then TrtPacks := val
      else if key = 'RIFE' then RifePack := val;
    end;
  end;
  Result := True;
end;

procedure InitializeWizard;
var
  ExePath, trtLabel: String;
begin
  GpuName := '';
  HasNvidia := False;
  TrtPacks := '';
  RifePack := 'rife';

  ExtractTemporaryFile('{#UpdaterExe}');
  ExePath := ExpandConstant('{tmp}\{#UpdaterExe}');
  RunRecommend(ExePath);

  CompPage := CreateInputOptionPage(wpSelectTasks,
    'Components', 'Choose which AI components to install for your hardware.',
    'Selected components download after the core files are copied. You can change ' +
    'this any time from the AnimeJaNai Manager (Ctrl+E in the player).',
    False, False);

  if HasNvidia then
    trtLabel := 'Upscaling (TensorRT) - for ' + GpuName
  else
    trtLabel := 'Upscaling (TensorRT) - requires an NVIDIA GPU';
  CompPage.Add(trtLabel);
  CompPage.Add('RIFE frame interpolation models');

  // TensorRT is NVIDIA-only; non-NVIDIA machines use the built-in DirectML
  // engine (already in the core), so disable and uncheck it there.
  CompPage.Values[0] := HasNvidia;
  CompPage.CheckListBox.ItemEnabled[0] := HasNvidia;
  CompPage.Values[1] := True;
end;

// Install the comma-separated packs in CSV via the installed updater. Updates the
// visible status label per pack (downloads are large). Returns the count that failed.
function InstallPacks(Csv: String): Integer;
var
  ExePath, pack: String;
  comma, rc: Integer;
begin
  Result := 0;
  ExePath := ExpandConstant('{app}\{#UpdaterExe}');
  Csv := Trim(Csv);
  while Csv <> '' do
  begin
    comma := Pos(',', Csv);
    if comma > 0 then
    begin
      pack := Copy(Csv, 1, comma - 1);
      Csv := Copy(Csv, comma + 1, Length(Csv) - comma);
    end
    else
    begin
      pack := Csv;
      Csv := '';
    end;
    pack := Trim(pack);
    if pack = '' then
      Continue;
    WizardForm.StatusLabel.Caption := 'Downloading component: ' + pack + ' (this may take a few minutes)...';
    WizardForm.Refresh;
    if not Exec(ExePath, '--install ' + pack, ExpandConstant('{app}'),
                SW_HIDE, ewWaitUntilTerminated, rc) or (rc <> 0) then
      Result := Result + 1;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  failed: Integer;
  packsToInstall: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // Refresh the shell so the new file associations take effect.
  SHChangeNotify($08000000, $0000, 0, 0);

  // Re-detect from the now-installed updater: network may be available now even
  // if it wasn't at wizard start, giving authoritative GPU-matched pack names.
  RunRecommend(ExpandConstant('{app}\{#UpdaterExe}'));

  failed := 0;
  if CompPage.Values[0] and (TrtPacks <> '') then
    failed := failed + InstallPacks(TrtPacks);
  if CompPage.Values[1] and (RifePack <> '') then
  begin
    packsToInstall := RifePack;
    failed := failed + InstallPacks(packsToInstall);
  end;

  WizardForm.StatusLabel.Caption := '';
  if failed > 0 then
    MsgBox('Some components could not be downloaded (you may be offline). ' +
           'AnimeJaNai will still play video; open the AnimeJaNai Manager ' +
           '(Ctrl+E in the player) later to finish installing them.',
           mbInformation, MB_OK);
end;
