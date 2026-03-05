; Fingerprint Bridge - Inno Setup Installer Script
; Build the C# project first, then compile this with Inno Setup 6+
;
; This installer:
;   1. Installs the DigitalPersona RTE (drivers) silently if not already installed
;   2. Installs FingerprintBridge.exe + native DLLs
;   3. Configures auto-start, firewall, URL ACL
;   4. Launches the tray application

#define MyAppName "Fingerprint Bridge"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Your Company"
#define MyAppURL "https://yourcompany.com"
#define MyAppExeName "FingerprintBridge.exe"

; Path to the published output
#define PublishDir "..\bin\Release\net8.0-windows\win-x64\publish"

; DigitalPersona RTE product code (from Setup.ini) — used to detect if already installed
#define DP_RTE_UpgradeCode "{7B16D7A8-EC3A-4FCC-9915-BD1D1D496E89}"
#define DP_RTE_ProductVersion "3.6.1"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=FingerprintBridge-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\assets\fingerprint.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardStyle=modern
CloseApplications=force

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "startup"; Description: "Start automatically when Windows starts"; GroupDescription: "Startup:"; Flags: checkedonce
Name: "firewall"; Description: "Add Windows Firewall exception (localhost only)"; GroupDescription: "Network:"; Flags: checkedonce

[Files]
; === DigitalPersona RTE installer (bundled) ===
; These are extracted to a temp dir and run during install if needed
Source: "prerequisites\rte_x64\setup.exe"; DestDir: "{tmp}\dp_rte"; Flags: ignoreversion deleteafterinstall
Source: "prerequisites\rte_x64\setup.msi"; DestDir: "{tmp}\dp_rte"; Flags: ignoreversion deleteafterinstall
Source: "prerequisites\rte_x64\Data1.cab"; DestDir: "{tmp}\dp_rte"; Flags: ignoreversion deleteafterinstall
Source: "prerequisites\rte_x64\0x0409.ini"; DestDir: "{tmp}\dp_rte"; Flags: ignoreversion deleteafterinstall
Source: "prerequisites\rte_x64\Setup.ini"; DestDir: "{tmp}\dp_rte"; Flags: ignoreversion deleteafterinstall
Source: "prerequisites\rte_x64\ISSetupPrerequisites\*"; DestDir: "{tmp}\dp_rte\ISSetupPrerequisites"; Flags: ignoreversion recursesubdirs createallsubdirs deleteafterinstall

; === Main application ===
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
; Launch the tray application after install
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Fingerprint Bridge"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop running process before uninstall
Filename: "taskkill"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillApp"

[Registry]
; Add to startup (conditional on task)
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FingerprintBridge"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Code]

// -----------------------------------------------------------------------
//  Detect whether DigitalPersona RTE is already installed
//  by checking the Windows Installer UpgradeCode in the registry
// -----------------------------------------------------------------------
function IsDpRteInstalled(): Boolean;
var
  SubKey: String;
begin
  Result := False;

  // Check 64-bit uninstall registry for DigitalPersona products
  // The UpgradeCode is registered by MSI under this path
  SubKey := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7FC7AAC6-4A7E-4DA4-92ED-D37FB6BDCA18}';
  if RegKeyExists(HKLM, SubKey) then
  begin
    Result := True;
    Exit;
  end;

  // Also check the Wow6432Node (32-bit view)
  SubKey := 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{7FC7AAC6-4A7E-4DA4-92ED-D37FB6BDCA18}';
  if RegKeyExists(HKLM, SubKey) then
  begin
    Result := True;
    Exit;
  end;

  // Alternate detection: check if dpfpdd.dll exists in System32
  if FileExists(ExpandConstant('{sys}\dpfpdd.dll')) then
  begin
    Result := True;
    Exit;
  end;

  // Alternate detection: check for the DP host service
  if RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\DpHost') then
  begin
    Result := True;
    Exit;
  end;
end;

// -----------------------------------------------------------------------
//  Install the DigitalPersona RTE silently
// -----------------------------------------------------------------------
function InstallDpRte(): Boolean;
var
  ResultCode: Integer;
  RtePath: String;
begin
  Result := True;
  RtePath := ExpandConstant('{tmp}\dp_rte\setup.exe');

  WizardForm.StatusLabel.Caption := 'Installing DigitalPersona fingerprint reader drivers...';
  WizardForm.StatusLabel.Update;

  // Silent install, suppress reboot
  // /s = silent setup.exe wrapper
  // /v"..." = arguments passed to msiexec
  // /qn = quiet MSI, no UI
  // REBOOT=ReallySuppress = don't reboot automatically
  if not Exec(
    RtePath,
    '/s /v"REBOOT=ReallySuppress /qn /l*v ' + ExpandConstant('"{tmp}\dp_rte_install.log"') + '"',
    ExpandConstant('{tmp}\dp_rte'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  ) then
  begin
    MsgBox('Failed to launch DigitalPersona driver installer.' + #13#10 +
           'Error code: ' + IntToStr(ResultCode) + #13#10#13#10 +
           'You may need to install the drivers manually.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  // setup.exe returns 0 on success, 3010 on success-needs-reboot
  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    MsgBox('DigitalPersona driver installation returned code: ' + IntToStr(ResultCode) + '.' + #13#10#13#10 +
           'The installation will continue, but the fingerprint reader may not work' + #13#10 +
           'until the drivers are installed correctly.' + #13#10#13#10 +
           'Check the log at: ' + ExpandConstant('{tmp}\dp_rte_install.log'), mbInformation, MB_OK);
  end;

  if ResultCode = 3010 then
  begin
    MsgBox('DigitalPersona drivers were installed successfully.' + #13#10#13#10 +
           'A system restart is recommended for the drivers to take full effect.' + #13#10 +
           'The fingerprint reader may not be detected until you restart.',
           mbInformation, MB_OK);
  end;
end;

// -----------------------------------------------------------------------
//  Firewall rule management
// -----------------------------------------------------------------------
procedure AddFirewallRule();
var
  ResultCode: Integer;
begin
  if IsTaskSelected('firewall') then
  begin
    Exec('netsh', 'advfirewall firewall add rule name="Fingerprint Bridge" dir=in action=allow program="' +
         ExpandConstant('{app}\{#MyAppExeName}') + '" enable=yes localip=127.0.0.1 remoteip=127.0.0.1',
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure RemoveFirewallRule();
var
  ResultCode: Integer;
begin
  Exec('netsh', 'advfirewall firewall delete rule name="Fingerprint Bridge"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// -----------------------------------------------------------------------
//  URL ACL so HttpListener works without admin at runtime
// -----------------------------------------------------------------------
procedure AddUrlAcl();
var
  ResultCode: Integer;
begin
  Exec('netsh', 'http add urlacl url=http://127.0.0.1:27015/ user=Everyone',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveUrlAcl();
var
  ResultCode: Integer;
begin
  Exec('netsh', 'http delete urlacl url=http://127.0.0.1:27015/',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// -----------------------------------------------------------------------
//  Main install flow
// -----------------------------------------------------------------------
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    // Step 1: Install DP RTE if not already present
    if not IsDpRteInstalled() then
    begin
      Log('DigitalPersona RTE not detected — installing...');
      InstallDpRte();
    end
    else
    begin
      Log('DigitalPersona RTE already installed — skipping driver install.');
    end;

    // Step 2: Firewall rule
    AddFirewallRule();

    // Step 3: URL ACL reservation
    AddUrlAcl();
  end;
end;

// -----------------------------------------------------------------------
//  Uninstall cleanup
// -----------------------------------------------------------------------
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveFirewallRule();
    RemoveUrlAcl();
    // Note: we do NOT uninstall the DP RTE — other applications may depend on it
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  // Kill the running application before uninstalling
  Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000);
  Result := True;
end;
