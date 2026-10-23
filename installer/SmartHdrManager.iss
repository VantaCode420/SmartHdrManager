; ---------------------------------------------------------------------------
; Smart HDR Manager - Windows installer (Inno Setup 6.6 or newer, Inno Setup 7 included)
;
; release.ps1 builds the installer next to the zips:
;   SmartHdrManager-vX.Y.Z-setup.exe             x64 and ARM64: installs the standalone build that fits the PC
;   SmartHdrManager-vX.Y.Z-win-x64-setup.exe     with release.ps1 -Arch x64: the x64 standalone build only
;   SmartHdrManager-vX.Y.Z-win-arm64-setup.exe   with release.ps1 -Arch arm64: the ARM64 standalone build only
;
; Per user, no administrator rights: the app goes to %LocalAppData%\Programs\Smart HDR Manager, so
; "Check for updates" can replace the exe like in an unzipped copy. Settings stay in %AppData%\SmartHdrManager.
;
; By hand (the folders are standalone package folders made by release.ps1):
;   ISCC.exe /DArch=all /DSourceDirX64="X64_DIR" /DSourceDirArm64="ARM64_DIR" installer\SmartHdrManager.iss
;   ISCC.exe /DArch=x64 /DSourceDir="X64_DIR" installer\SmartHdrManager.iss
; Optional: /DAppVersion=X.Y.Z (default: the version of SmartHdrManager.exe), /DOutputDir="FOLDER" (default: installer\Output).
; The file is UTF-8 with BOM (Inno Setup needs the BOM for the translated messages).
; ---------------------------------------------------------------------------

#if Ver < EncodeVer(6, 6, 0)
  #error Inno Setup 6.6 or newer is required (https://jrsoftware.org/isdl.php)
#endif

#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "all"
  #if !Defined(SourceDirX64) || !Defined(SourceDirArm64)
    #error Arch=all needs SourceDirX64 and SourceDirArm64: the two standalone package folders (see the top of this file)
  #endif
  ; README, LICENSE etc. are the same in both packages
  #define SourceDir SourceDirX64
  #define ArchAllowed "x64compatible or arm64"
  #define SetupSuffix "-setup"
#else
  #ifndef SourceDir
    #error Define SourceDir: the folder with SmartHdrManager.exe, README.md, LICENSE.txt and the userscript (see the top of this file)
  #endif
  #if Arch == "x64"
    ; Also installs on Windows 11 on Arm (x64 emulation); the arm64 or combined installer is the better fit there
    #define ArchAllowed "x64compatible"
  #elif Arch == "arm64"
    #define ArchAllowed "arm64"
  #else
    #error Arch must be all, x64 or arm64
  #endif
  #define SetupSuffix "-win-" + Arch + "-setup"
#endif
#ifndef AppVersion
  #define VerMajor
  #define VerMinor
  #define VerRev
  #define VerBuild
  #expr GetVersionComponents(AddBackslash(SourceDir) + "SmartHdrManager.exe", VerMajor, VerMinor, VerRev, VerBuild)
  #define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)
#endif
#ifndef OutputDir
  #define OutputDir "installer\Output"
#endif

#define AppName "Smart HDR Manager"
#define AppExe "SmartHdrManager.exe"
; Never change it: Windows finds an existing installation by this id (also used in Updater.cs)
#define AppGuid "2D815E3F-DDE7-4630-9749-D3885F39D122"
; Same as Program.AppUserModelId: notifications and taskbar pins belong to the Start menu shortcut
#define AppUserModelId "VantaCode420.SmartHdrManager"
#define RepoUrl "https://github.com/VantaCode420/SmartHdrManager"

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=VantaCode420
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (c) 2026 VantaCode420
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
; Without the version: "Check for updates" updates the installed exe, so a version in the name would go stale
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
; For all users (Program Files) only with /ALLUSERS: "Check for updates" cannot write there
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
MinVersion=10.0
SourceDir=..
OutputDir={#OutputDir}
OutputBaseFilename=SmartHdrManager-v{#AppVersion}{#SetupSuffix}
SetupIconFile=app.ico
WizardStyle=modern dynamic
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "nl"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "cs"; MessagesFile: "compiler:Languages\Czech.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

; The app itself is in English. Messages without a translation below are shown in English.
[CustomMessages]
StartupGroup=Startup:
AutostartTask=Start with Windows (hidden in the tray)
DeleteUserData=Also delete your settings, game list and log?%n%n%1%n%2%n%nChoose No to keep them for a later installation.
es.StartupGroup=Inicio:
es.AutostartTask=Iniciar con Windows (oculto en la bandeja del sistema)
es.DeleteUserData=¿Eliminar también tu configuración, la lista de juegos y el registro?%n%n%1%n%2%n%nElige No para conservarlos para una instalación futura.
de.StartupGroup=Autostart:
de.AutostartTask=Mit Windows starten (im Infobereich)
de.DeleteUserData=Auch Einstellungen, Spieleliste und Protokoll löschen?%n%n%1%n%2%n%nWähle Nein, um sie für eine spätere Installation zu behalten.
fr.StartupGroup=Démarrage :
fr.AutostartTask=Démarrer avec Windows (dans la zone de notification)
fr.DeleteUserData=Supprimer aussi vos paramètres, la liste des jeux et le journal ?%n%n%1%n%2%n%nChoisissez Non pour les conserver pour une installation ultérieure.
ptbr.StartupGroup=Inicialização:
ptbr.AutostartTask=Iniciar com o Windows (oculto na bandeja do sistema)
ptbr.DeleteUserData=Excluir também suas configurações, a lista de jogos e o log?%n%n%1%n%2%n%nEscolha Não para mantê-los para uma instalação futura.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; First installation only: after that the switch in the app's Settings decides (an update must not turn it back on)
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:StartupGroup}"; Check: not IsAdminInstallMode and not IsUpgrade

[Files]
#if Arch == "all"
; x64 first: PrepareToInstall extracts the first SmartHdrManager.exe entry (it also runs on Windows 11 on Arm)
Source: "{#SourceDirX64}\{#AppExe}"; DestDir: "{app}"; Check: not IsArm64; Flags: ignoreversion
Source: "{#SourceDirArm64}\{#AppExe}"; DestDir: "{app}"; Check: IsArm64; Flags: ignoreversion
#else
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
#endif
Source: "{#SourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\SmartHdrManager_YouTube.user.js"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; The same entry as Settings › Start with Windows
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SmartHdrManager"; ValueData: """{app}\{#AppExe}"" --tray"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Close the running app (it switches HDR back if it turned it on), then remove its registry entries:
; Start with Windows, the "Play with HDR" / "(with HDR)" player entries and the notification name
Filename: "{app}\{#AppExe}"; Parameters: "--exit"; RunOnceId: "ExitApp"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; RunOnceId: "Unregister"; Flags: runhidden waituntilterminated

[UninstallDelete]
; Left behind by "Check for updates" until the next start
Type: files; Name: "{app}\{#AppExe}.old"

[Code]
function IsUpgrade: Boolean;
begin
  Result := RegKeyExists(HKA, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#AppGuid}}_is1');
end;

// Before files are replaced: ask a running copy to exit the normal way (HDR is switched back, settings are saved).
// The new exe does it, so it also works when the installed version is older. Copies too old to know --exit are left
// to CloseApplications (Windows Restart Manager).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if CheckForMutexes('Local\SmartHdrManager.SingleInstance') then
  begin
    ExtractTemporaryFile('{#AppExe}');
    Exec(ExpandConstant('{tmp}\{#AppExe}'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

// Settings (%AppData%) and log (%LocalAppData%) are kept unless the user says otherwise.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Settings, Logs: String;
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent or IsAdminInstallMode then
    Exit;
  Settings := ExpandConstant('{userappdata}\SmartHdrManager');
  Logs := ExpandConstant('{localappdata}\SmartHdrManager');
  if not DirExists(Settings) and not DirExists(Logs) then
    Exit;
  if MsgBox(FmtMessage(CustomMessage('DeleteUserData'), [Settings, Logs]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Settings, True, True, True);
    DelTree(Logs, True, True, True);
  end;
end;
