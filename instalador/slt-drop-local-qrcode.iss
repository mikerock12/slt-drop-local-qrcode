; Instalador do SLT Drop Local QRCode.
;
; Gera um Setup.exe único que instala o programa, cria os atalhos, registra o
; desinstalador em Adicionar/Remover Programas e cria a regra de entrada no
; Firewall do Windows. O executável já é autocontido (runtime do .NET, Windows
; Forms, ASP.NET Core/Kestrel e QRCoder dentro dele): não há nada para instalar antes.
;
; Compile por scripts\gerar-instalador.ps1, que roda os testes, publica o
; programa e chama o ISCC com a versão de Directory.Build.props.

#ifndef Versao
  #define Versao "1.0.0"
#endif

#define Nome "SLT Drop Local QRCode"
#define Publicador "Smells Like Tech"
#define ExeNome "SLTDropLocalQRCode.exe"
#define Site "https://github.com/mikerock12/slt-drop-local-qrcode"
#define RegraFirewall "SLT Drop Local QRCode"

[Setup]
; NÃO trocar: é por este AppId que o Windows reconhece uma instalação existente
; e atualiza no lugar em vez de criar uma segunda cópia.
AppId={{A2B43719-F39E-4EAD-8AC7-29850C1ECDFE}
AppName={#Nome}
AppVersion={#Versao}
AppVerName={#Nome} {#Versao}
VersionInfoVersion={#Versao}
VersionInfoDescription=Instalador do {#Nome}
AppPublisher={#Publicador}
AppPublisherURL={#Site}
AppSupportURL={#Site}
DefaultDirName={autopf}\{#Nome}
DefaultGroupName={#Nome}
UninstallDisplayName={#Nome}
UninstallDisplayIcon={app}\{#ExeNome}
OutputDir=..\entrega
OutputBaseFilename=SLT-Drop-Local-QRCode-Setup-{#Versao}
SetupIconFile=..\assets\icon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Administrador é obrigatório: só assim dá para criar a regra do firewall, que é
; o que deixa o celular alcançar o PC.
PrivilegesRequired=admin

; Se o programa estiver aberto, o instalador oferece fechá-lo em vez de falhar.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "..\publish\{#ExeNome}"; DestDir: "{app}"; Flags: ignoreversion
Source: "LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#Nome}"; Filename: "{app}\{#ExeNome}"
Name: "{group}\Desinstalar {#Nome}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#Nome}"; Filename: "{app}\{#ExeNome}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeNome}"; Description: "Abrir o {#Nome} agora"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: dirifempty; Name: "{app}"

[Code]
const
  NomeRegra = '{#RegraFirewall}';

function Netsh(const Parametros: String): Integer;
var
  Codigo: Integer;
begin
  if Exec(ExpandConstant('{sys}\netsh.exe'), Parametros, '', SW_HIDE, ewWaitUntilTerminated, Codigo) then
    Result := Codigo
  else
    Result := -1;
  Log(Format('netsh %s -> %d', [Parametros, Result]));
end;

procedure RemoverRegraFirewall;
begin
  // Pode não existir (primeira instalação): o código de saída não importa aqui.
  Netsh('advfirewall firewall delete rule name="' + NomeRegra + '"');
end;

procedure CriarRegraFirewall;
var
  Programa: String;
begin
  Programa := ExpandConstant('{app}\{#ExeNome}');
  // Remove antes de criar: reinstalar nunca deixa regras duplicadas.
  RemoverRegraFirewall;
  // Regra por programa, não por porta: vale qualquer que seja a porta livre que o
  // programa conseguir abrir. profile=any porque muitos Windows classificam o Wi-Fi
  // de casa como rede "Pública"; o acesso continua exigindo o token do QR Code.
  if Netsh('advfirewall firewall add rule name="' + NomeRegra + '" dir=in action=allow ' +
           'program="' + Programa + '" protocol=TCP profile=any enable=yes ' +
           'description="Permite que celulares da rede local enviem arquivos para o SLT Drop Local QRCode."') <> 0 then
    SuppressibleMsgBox('Não foi possível criar a regra no Firewall do Windows.' + #13#10#13#10 +
      'O programa foi instalado, mas o celular pode não conseguir se conectar. ' +
      'Tente reinstalar, ou libere o programa manualmente no Firewall do Windows.',
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    CriarRegraFirewall;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoverRegraFirewall;
end;
