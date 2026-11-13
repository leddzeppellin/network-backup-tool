; Instalador do Network Backup Tool (versão instalável).
; Compilar: ISCC /DAppVersion=2.6.0 /DSourceExe=..\publish\NetBackupTool.exe installer\NetBackupTool.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\NetBackupTool.exe"
#endif

[Setup]
AppId={{8D5B2C1E-7A4F-4E2B-9C61-3F0A1B2C4D5E}
AppName=Network Backup Tool
AppVersion={#AppVersion}
AppVerName=Network Backup Tool {#AppVersion}
AppPublisher=Ricardo Valério da Silva
AppPublisherURL=https://github.com/leddzeppellin/network-backup-tool
AppSupportURL=https://github.com/leddzeppellin/network-backup-tool/issues
DefaultDirName={autopf}\Network Backup Tool
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publish-setup
OutputBaseFilename=NetBackupTool-Setup
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\NetBackupTool.exe
UninstallDisplayName=Network Backup Tool
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
pt.RoleTitle=Como este computador vai ser usado?
pt.RoleDesc=Dá para mudar depois desinstalando e instalando de novo.
pt.RoleSub=Escolha uma opção:
pt.RoleServer=Servidor — recebe e guarda os arquivos da equipe, o tempo todo, mesmo sem ninguém logado
pt.RoleStation=Estação de trabalho — usa o programa para enviar e receber arquivos
pt.DestTitle=Onde o servidor guarda os arquivos recebidos?
pt.DestDesc=Cada computador que enviar ganha uma subpasta com o próprio nome.
pt.DestSub=Pasta de destino:
pt.StatusService=Configurando o serviço do Windows...
pt.StatusFirewall=Liberando o programa no firewall...
pt.DesktopIcon=Criar atalho na Área de Trabalho

en.RoleTitle=How will this computer be used?
en.RoleDesc=You can change it later by uninstalling and installing again.
en.RoleSub=Choose one option:
en.RoleServer=Server — receives and keeps the team's files, all the time, even with nobody signed in
en.RoleStation=Workstation — uses the program to send and receive files
en.DestTitle=Where should the server keep received files?
en.DestDesc=Each computer that sends files gets a subfolder with its own name.
en.DestSub=Destination folder:
en.StatusService=Setting up the Windows service...
en.StatusFirewall=Allowing the program through the firewall...
en.DesktopIcon=Create a desktop shortcut

es.RoleTitle=¿Cómo se va a usar este equipo?
es.RoleDesc=Se puede cambiar después desinstalando e instalando de nuevo.
es.RoleSub=Elige una opción:
es.RoleServer=Servidor: recibe y guarda los archivos del equipo todo el tiempo, incluso sin nadie con sesión iniciada
es.RoleStation=Estación de trabajo: usa el programa para enviar y recibir archivos
es.DestTitle=¿Dónde guarda el servidor los archivos recibidos?
es.DestDesc=Cada equipo que envía archivos recibe una subcarpeta con su propio nombre.
es.DestSub=Carpeta de destino:
es.StatusService=Configurando el servicio de Windows...
es.StatusFirewall=Permitiendo el programa en el firewall...
es.DesktopIcon=Crear un acceso directo en el escritorio

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Network Backup Tool"; Filename: "{app}\NetBackupTool.exe"
Name: "{autodesktop}\Network Backup Tool"; Filename: "{app}\NetBackupTool.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Network Backup Tool"; ValueType: string; ValueName: "Role"; ValueData: "{code:GetRole}"; Flags: uninsdeletekey

[Run]
; Regra de entrada só para o programa, em todos os perfis de rede (rede de cliente costuma estar como "Pública").
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Network Backup Tool"""; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StatusFirewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""Network Backup Tool"" dir=in action=allow program=""{app}\NetBackupTool.exe"" enable=yes profile=any"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StatusFirewall}"
Filename: "{app}\NetBackupTool.exe"; Parameters: "--instalar-servico ""{code:GetDest}"""; Check: IsServer; Flags: runhidden waituntilterminated; StatusMsg: "{cm:StatusService}"
Filename: "{app}\NetBackupTool.exe"; Description: "{cm:LaunchProgram,Network Backup Tool}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Atualização silenciosa (pelo próprio app): reabre o programa no fim.
Filename: "{app}\NetBackupTool.exe"; Check: WizardSilent; Flags: nowait runasoriginaluser

[UninstallRun]
Filename: "{app}\NetBackupTool.exe"; Parameters: "--remover-servico"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Network Backup Tool"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveFirewall"

[UninstallDelete]
; Só a configuração do serviço; os arquivos recebidos ficam onde estão.
Type: filesandordirs; Name: "{commonappdata}\NetBackupTool"

[Code]
var
  RolePage: TInputOptionWizardPage;
  DestPage: TInputDirWizardPage;
  PreviousRole: String;

function IsServer: Boolean;
begin
  if PreviousRole <> '' then
    Result := PreviousRole = 'Servidor'
  else
    Result := RolePage.SelectedValueIndex = 0;
end;

function GetRole(Param: String): String;
begin
  if IsServer then Result := 'Servidor' else Result := 'Estacao';
end;

function GetDest(Param: String): String;
begin
  { Atualização: string vazia mantém a pasta já configurada no serviço. }
  if PreviousRole <> '' then Result := '' else Result := DestPage.Values[0];
end;

procedure InitializeWizard;
begin
  if not RegQueryStringValue(HKLM, 'Software\Network Backup Tool', 'Role', PreviousRole) then
    PreviousRole := '';

  RolePage := CreateInputOptionPage(wpSelectDir, CustomMessage('RoleTitle'), CustomMessage('RoleDesc'),
    CustomMessage('RoleSub'), True, False);
  RolePage.Add(CustomMessage('RoleServer'));
  RolePage.Add(CustomMessage('RoleStation'));
  RolePage.SelectedValueIndex := 1;

  DestPage := CreateInputDirPage(RolePage.ID, CustomMessage('DestTitle'), CustomMessage('DestDesc'),
    CustomMessage('DestSub'), False, '');
  DestPage.Add('');
  DestPage.Values[0] := ExpandConstant('{sd}\Backups');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  { Atualização: o papel e a pasta já estão decididos. }
  if (PreviousRole <> '') and ((PageID = RolePage.ID) or (PageID = DestPage.ID)) then
    Result := True
  else if PageID = DestPage.ID then
    Result := not IsServer;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  { O executável em uso pelo serviço não pode ser trocado: para o serviço antes de copiar. }
  Exec(ExpandConstant('{sys}\net.exe'), 'stop NetBackupTool', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
