#define AppVersion "1.0.0"

[Setup]
AppId={{6E0D3E58-1D9B-41E6-8F41-B391B0736DE9}
AppName=PULSE
AppVersion={#AppVersion}
AppPublisher=PULSE
DefaultDirName={localappdata}\Programs\PULSE
DefaultGroupName=PULSE
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\website\downloads
OutputBaseFilename=PulseSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Pulse.exe
CloseApplications=yes
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"

[Files]
Source: "..\website\downloads\Pulse.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\PULSE"; Filename: "{app}\Pulse.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PULSE"; Filename: "{app}\Pulse.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Pulse.exe"; Description: "Launch PULSE"; Flags: postinstall nowait skipifsilent

[Code]
function HasDotNetDesktopRuntime(): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
  Installed: Cardinal;
  RuntimeKey: String;
begin
  Result := False;
  RuntimeKey := 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  if RegGetValueNames(HKLM32, RuntimeKey, Versions) then
    for I := 0 to GetArrayLength(Versions) - 1 do
      if (Copy(Versions[I], 1, 4) = '8.0.') and
         RegQueryDWordValue(HKLM32, RuntimeKey, Versions[I], Installed) and
         (Installed = 1) then
      begin
        Result := True;
        Exit;
      end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := HasDotNetDesktopRuntime();
  if not Result then
  begin
    if MsgBox(
      'PULSE requires the Microsoft .NET 8 Desktop Runtime (x64). Open the official download page now?',
      mbConfirmation,
      MB_YESNO) = IDYES then
      ShellExec('', 'https://dotnet.microsoft.com/en-us/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
