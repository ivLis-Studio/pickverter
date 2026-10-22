; Per-user installer (no administrator rights, so the app can update itself in place).
;   ISCC /DVersion=0.2.0 /DArch=x64 /DSource=..\dist\0.2.0\publish-x64\Pickverter installer\Pickverter.iss
#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef Source
  #define Source "..\dist\publish-" + Arch + "\Pickverter"
#endif

[Setup]
; Keep in sync with Installer.UninstallKey in src/Pickverter/Services/UpdateInstaller.cs.
AppId={{8C3C8B57-5D0F-4E8B-9B7A-6F1E1B5C2A10}
AppName=Pickverter
AppVersion={#Version}
AppVerName=Pickverter {#Version}
AppPublisher=ivLis Studio
AppPublisherURL=https://github.com/ivLis-Studio/pickverter
AppSupportURL=https://github.com/ivLis-Studio/pickverter/issues
AppUpdatesURL=https://github.com/ivLis-Studio/pickverter/releases
AppCopyright=© 2026 ivLis Studio · MIT License
VersionInfoVersion={#Copy(Version, 1, Pos("-", Version + "-") - 1)}
DefaultDirName={localappdata}\Programs\Pickverter
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputBaseFilename=Pickverter-{#Version}-win-{#Arch}-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Pickverter\Resources\Pickverter.ico
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
WizardImageFile=wizard-large.bmp,wizard-large-200.bmp
UninstallDisplayIcon={app}\Pickverter.exe
UninstallDisplayName=Pickverter
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
CloseApplications=no
SetupMutex=ivLisStudio.Pickverter.Setup

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#if FileExists(CompilerPath + "Languages\Korean.isl")
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"
#endif
#if FileExists(CompilerPath + "Languages\Japanese.isl")
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
#endif
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "zhhans"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
#if FileExists(CompilerPath + "Languages\ChineseTraditional.isl")
Name: "zhhant"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
#endif
#if FileExists(CompilerPath + "Languages\Spanish.isl")
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
#endif
#if FileExists(CompilerPath + "Languages\French.isl")
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
#endif
#if FileExists(CompilerPath + "Languages\German.isl")
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
#endif
#if FileExists(CompilerPath + "Languages\Russian.isl")
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
#endif
#if FileExists(CompilerPath + "Languages\BrazilianPortuguese.isl")
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Leftovers of an interrupted in-place update.
Type: filesandordirs; Name: "{app}.old"

[Icons]
Name: "{autoprograms}\Pickverter"; Filename: "{app}\Pickverter.exe"
Name: "{autodesktop}\Pickverter"; Filename: "{app}\Pickverter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Pickverter.exe"; Description: "{cm:LaunchProgram,Pickverter}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\Pickverter.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitPickverter"

[UninstallDelete]
Type: filesandordirs; Name: "{app}.old"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  { A running copy is asked to quit so its files can be replaced. }
  if FileExists(ExpandConstant('{app}\Pickverter.exe')) then
    Exec(ExpandConstant('{app}\Pickverter.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Pickverter');
end;

