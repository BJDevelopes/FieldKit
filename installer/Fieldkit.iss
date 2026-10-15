; SPDX-License-Identifier: GPL-3.0-or-later
; Fieldkit's installer (Inno Setup 6). The release workflow builds one per architecture from the published exe:
;   ISCC /DAppVersion=0.3.0 /DArch=x64 installer\Fieldkit.iss      -> artifacts\Fieldkit-0.3.0-setup-x64.exe
; It installs for the current user (no administrator prompt), upgrades in place, and its uninstaller removes what
; Fieldkit registered (Start with Windows, the browser picker) while keeping settings in %APPDATA%\Fieldkit.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; Windows file versions are numbers only: 0.4.0-beta.1 -> 0.4.0.
#define FileVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1)
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceExe
  #define SourceExe "..\artifacts\win-" + Arch + "\Fieldkit.exe"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{96CB0153-5CCF-4CB7-81B0-D8127AA944F9}
AppName=Fieldkit
AppVersion={#AppVersion}
AppVerName=Fieldkit {#AppVersion}
AppPublisher=BJDevelopes
AppPublisherURL=https://github.com/BJDevelopes/FieldKit
AppSupportURL=https://github.com/BJDevelopes/FieldKit/issues
AppUpdatesURL=https://github.com/BJDevelopes/FieldKit/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
; Per user, in %LOCALAPPDATA%\Programs\Fieldkit; "Install for all users" is offered on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\Fieldkit
DisableProgramGroupPage=yes
DisableReadyPage=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Fieldkit-{#AppVersion}-setup-{#Arch}
SetupIconFile=..\src\Fieldkit\Assets\Fieldkit.ico
UninstallDisplayIcon={app}\Fieldkit.exe
UninstallDisplayName=Fieldkit
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
; A running copy of the installed Fieldkit is closed before its files are replaced.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Fieldkit"; Filename: "{app}\Fieldkit.exe"
Name: "{autodesktop}\Fieldkit"; Filename: "{app}\Fieldkit.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Fieldkit.exe"; Description: "Start Fieldkit"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Turns off Start with Windows and removes the browser picker's registration, for this user.
Filename: "{app}\Fieldkit.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "FieldkitCleanup"

[Code]
// Closes the Fieldkit that runs from the install folder (a portable copy elsewhere is left alone).
procedure StopInstalledFieldkit();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process Fieldkit -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + ExpandConstant('{app}') + '\*'' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopInstalledFieldkit();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopInstalledFieldkit();
end;
