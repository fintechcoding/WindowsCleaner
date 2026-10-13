#define AppVersion "1.0.0"
[Setup]
AppId={{30F90C12-B778-4298-A060-D7FEFBEAB4A2}
AppName=Windows Cleaner
AppVersion={#AppVersion}
AppPublisher=Windows Cleaner
DefaultDirName={autopf}\WindowsCleaner
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
WizardStyle=modern
OutputDir=..\artifacts\installer
OutputBaseFilename=WindowsCleaner-Setup-1.0.0
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\WindowsCleaner.exe
CloseApplications=yes
RestartApplications=no
InfoBeforeFile=..\README.md
[Languages]
Name: english; MessagesFile: compiler:Default.isl
[Files]
Source: ..\artifacts\payload\*; DestDir: {app}; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: Uninstall.exe
[Icons]
Name: {commonprograms}\Windows Cleaner; Filename: {app}\WindowsCleaner.exe
[Registry]
Root: HKLM; Subkey: SOFTWARE\Microsoft\Windows\CurrentVersion\Run; ValueType: string; ValueName: WindowsCleaner; ValueData: """{app}\WindowsCleaner.exe"""; Flags: uninsdeletevalue
[Run]
Filename: {app}\WindowsCleaner.exe; Flags: nowait runasoriginaluser; Description: Open Windows Cleaner
[Code]
type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint: Cardinal;
  end;
function OpenSCManager(machine, database: String; access: Cardinal): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenService(manager: THandle; name: String; access: Cardinal): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(service: THandle; var status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function CloseServiceHandle(handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

procedure Checked(const fileName, arguments: String);
var code: Integer;
begin
  if not Exec(fileName, arguments, '', SW_HIDE, ewWaitUntilTerminated, code) then
    RaiseException('Could not launch setup command.');
  if code <> 0 then RaiseException('Setup command failed: ' + fileName + ' (code ' + IntToStr(code) + ').');
end;

function ServiceExists: Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\WindowsCleaner');
end;

procedure StopService;
var manager, service: THandle; status: TServiceStatus; attempts, code: Integer;
begin
  if FileExists(ExpandConstant('{app}\WindowsCleaner.exe')) then
    Checked(ExpandConstant('{app}\WindowsCleaner.exe'), '--quit');
  if not ServiceExists then exit;
  manager := OpenSCManager('', '', 1);
  if manager = 0 then RaiseException('Could not access the Service Control Manager.');
  try
    service := OpenService(manager, 'WindowsCleaner', 4);
    if service = 0 then RaiseException('Could not access Windows Cleaner service.');
    try
      Exec(ExpandConstant('{sys}\sc.exe'), 'stop WindowsCleaner', '', SW_HIDE, ewWaitUntilTerminated, code);
      for attempts := 1 to 90 do begin
        if not QueryServiceStatus(service, status) then RaiseException('Could not query service state.');
        if status.CurrentState = 1 then exit;
        Sleep(500);
      end;
      RaiseException('Windows Cleaner did not stop within 45 seconds.');
    finally CloseServiceHandle(service); end;
  finally CloseServiceHandle(manager); end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  try
    if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\WindowsCleaner')) <> 0 then
      RaiseException('The installation location must be Program Files\WindowsCleaner.');
    StopService;
  except Result := GetExceptionMessage; end;
end;

procedure CurStepChanged(step: TSetupStep);
var binaryPath: String;
begin
  if step <> ssPostInstall then exit;
  Checked(ExpandConstant('{app}\Runtime\dotnet.exe'), '"' + ExpandConstant('{app}\Service\Cleaner.Service.dll') + '" --initialize');
  binaryPath := '\"' + ExpandConstant('{app}\Runtime\dotnet.exe') + '\" \"' + ExpandConstant('{app}\Service\Cleaner.Service.dll') + '\"';
  if ServiceExists then
    Checked(ExpandConstant('{sys}\sc.exe'), 'config WindowsCleaner binPath= "' + binaryPath + '" start= auto')
  else
    Checked(ExpandConstant('{sys}\sc.exe'), 'create WindowsCleaner binPath= "' + binaryPath + '" start= auto DisplayName= "Windows Cleaner"');
  Checked(ExpandConstant('{sys}\sc.exe'), 'description WindowsCleaner "Scheduled privacy and temporary-file maintenance with system tray controls."');
  Checked(ExpandConstant('{sys}\sc.exe'), 'failure WindowsCleaner reset= 86400 actions= restart/30000/restart/60000/restart/120000');
  Checked(ExpandConstant('{sys}\sc.exe'), 'start WindowsCleaner');
end;

function InitializeUninstall: Boolean;
begin
  Result := False;
  try
    StopService;
    Checked(ExpandConstant('{app}\Runtime\dotnet.exe'), '"' + ExpandConstant('{app}\Service\Cleaner.Service.dll') + '" --restore');
    if ServiceExists then Checked(ExpandConstant('{sys}\sc.exe'), 'delete WindowsCleaner');
    Result := True;
  except MsgBox(GetExceptionMessage, mbError, MB_OK); end;
end;
