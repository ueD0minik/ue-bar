; SPDX-License-Identifier: GPL-3.0-or-later
; Copyright (C) 2026 Dominik Poschinger
; ue Bar – installer (M7, Inno Setup 6), German and English (R1)
; Called by app/build/publish.ps1:
;   ISCC.exe /DAppVersion=1.1.0 /DSourceDir=<publish folder> /DOutputDir=<dist> ueBar.iss
; Framework-dependent: checks for the .NET 10 Desktop Runtime and downloads/installs it from
; Microsoft if needed. Installs without admin rights to %LOCALAPPDATA%\Programs\ueBar.
; Language: picked automatically from the Windows display language (German → German, otherwise
; English); "/LANG=en" or "/LANG=de" on the command line forces one.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir missing (folder with the published ueBar.exe)
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "ue Bar"
#define AppExe "ueBar.exe"
; Runtime from Microsoft (always the latest 10.0 patch)
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{6F3C2A9E-4B7D-4E1A-9C55-2D8E7A1B3F40}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Dominik Poschinger
AppCopyright=Copyright (C) 2026 Dominik Poschinger
VersionInfoCompany=Dominik Poschinger
DefaultDirName={localappdata}\Programs\ueBar
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=ueBar-Setup-{#AppVersion}
SetupIconFile=..\src\UeBar.App\Assets\ueBar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The [Code] part ends a running ue Bar itself (cleanly through a signal).
CloseApplications=no
RestartApplications=no
; Language from the Windows display language; the dialog only appears if nothing matches.
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
en.TaskAutostart=Start with Windows
de.TaskAutostart=Mit Windows starten
en.IconComment=Slide-out dashboard at the top edge of the screen
de.IconComment=Ausfahrbares Dashboard am oberen Bildschirmrand
en.RunNow=Start ue Bar now
de.RunNow=ue Bar jetzt starten
en.RuntimeDownloading=The .NET 10 Desktop Runtime is being downloaded from Microsoft …
de.RuntimeDownloading=Die .NET 10 Desktop Runtime von Microsoft wird geladen …
en.RuntimeAdditionally=Additionally:
de.RuntimeAdditionally=Zusätzlich:
en.RuntimeMemo=.NET 10 Desktop Runtime from Microsoft (downloaded, about 60 MB, needs administrator rights once)
de.RuntimeMemo=.NET 10 Desktop Runtime von Microsoft (wird geladen, ca. 60 MB, braucht einmal Administratorrechte)
en.RuntimeInstalling=The .NET 10 Desktop Runtime is being installed …
de.RuntimeInstalling=Die .NET 10 Desktop Runtime wird installiert …
en.RuntimeConfirm=Please confirm the Windows prompt.
de.RuntimeConfirm=Bitte die Windows-Abfrage bestätigen.
en.RuntimeNotInstalled=The .NET 10 Desktop Runtime could not be installed (%1).%nPlease install it later yourself from https://dotnet.microsoft.com/download/dotnet/10.0.
de.RuntimeNotInstalled=Die .NET 10 Desktop Runtime konnte nicht installiert werden (%1).%nBitte später selbst von https://dotnet.microsoft.com/download/dotnet/10.0 installieren.
en.RuntimeFailed=Installing the .NET 10 Desktop Runtime failed (code %1).
de.RuntimeFailed=Die Installation der .NET 10 Desktop Runtime ist fehlgeschlagen (Code %1).
en.DeleteData=Should the settings, the layout cache and the logs be deleted as well?%n%n%1%n%n"No" keeps them for a later installation.
de.DeleteData=Sollen auch die Einstellungen, der Layout-Cache und die Logs gelöscht werden?%n%n%1%n%n„Nein“ behält sie für eine spätere Installation.
en.HaDeviceHint=Note: if you connected ue Bar with Home Assistant, the device "ue Bar – <name>" (Mobile App) is still listed there.%nYou can delete it in Home Assistant under Settings → Devices & services → Mobile App.
de.HaDeviceHint=Hinweis: Hast du ue Bar mit Home Assistant verbunden, steht dort noch das Gerät „ue Bar – <Name>“ (Mobile App).%nEs lässt sich in Home Assistant unter Einstellungen → Geräte & Dienste → Mobile App von Hand löschen.

[Tasks]
Name: "autostart"; Description: "{cm:TaskAutostart}"; Flags: checkedonce

[InstallDelete]
; Up to 1.1.1 the modules were assemblies in the app folder. Since 1.2.0 they are plugins in plugins\<id>\.
Type: files; Name: "{app}\UeBar.Modules.*.dll"
Type: files; Name: "{app}\de\UeBar.Modules.*.resources.dll"
; The bundled plugins are replaced as a whole: a file left over from an older version would no longer
; match the checksum list, and ue Bar would ask before loading its own plugin.
Type: filesandordirs; Name: "{app}\plugins"

[Files]
; Includes plugins\<id>\ (the plugins that come with ue Bar) and plugins\bundled.sha256.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:IconComment}"

[Registry]
; The same value the app switches in its settings (SPEC 3).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ueBar"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:RunNow}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  DownloadPage: TDownloadWizardPage;
  RuntimeNeeded: Boolean;

{ Is ue Bar running? (The app holds this lock while it runs.) }
function AppRunning(): Boolean;
begin
  Result := CheckForMutexes('Local\ueBar-SingleInstance');
end;

{ End a running ue Bar cleanly: set the signal, wait up to 6 s; older versions without the signal are killed. }
procedure StopRunningApp();
var
  Handle: THandle;
  I, Code: Integer;
begin
  if not AppRunning() then
    Exit;
  Handle := OpenEvent(EVENT_MODIFY_STATE or SYNCHRONIZE, False, 'Local\ueBar-Exit');
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;
  for I := 1 to 60 do
  begin
    if not AppRunning() then
      Exit;
    Sleep(100);
  end;
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
end;

{ Is the .NET 10 Desktop Runtime present? (folder of the installed runtimes) }
function DesktopRuntimeInstalled(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  RuntimeNeeded := not DesktopRuntimeInstalled();
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), CustomMessage('RuntimeDownloading'), @OnDownloadProgress);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine + MemoTasksInfo;
  if RuntimeNeeded then
    Result := Result + NewLine + NewLine + CustomMessage('RuntimeAdditionally') + NewLine + Space + CustomMessage('RuntimeMemo');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
begin
  Result := True;
  if (CurPageID = wpReady) and RuntimeNeeded then
  begin
    DownloadPage.Clear;
    DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-10-x64.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        DownloadPage.SetText(CustomMessage('RuntimeInstalling'), CustomMessage('RuntimeConfirm'));
        if not ShellExec('', ExpandConstant('{tmp}\windowsdesktop-runtime-10-x64.exe'), '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, Code) then
        begin
          MsgBox(FmtMessage(CustomMessage('RuntimeNotInstalled'), [SysErrorMessage(Code)]), mbError, MB_OK);
          Result := False;
        end
        else if (Code <> 0) and (Code <> 3010) and (Code <> 1641) then
        begin
          MsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(Code)]), mbError, MB_OK);
          Result := False;
        end
        else
          RuntimeNeeded := not DesktopRuntimeInstalled();
      except
        if DownloadPage.AbortedByUser then
          Log('Download cancelled')
        else
          SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

{ Uninstall: end the app, remove autostart, delete data on request, hint about Home Assistant. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningApp();
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ueBar');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}\ueBar');
    if DirExists(DataDir) and not UninstallSilent then
    begin
      if MsgBox(FmtMessage(CustomMessage('DeleteData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
    if not UninstallSilent then
      MsgBox(CustomMessage('HaDeviceHint'), mbInformation, MB_OK);
  end;
end;
