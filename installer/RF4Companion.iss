#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef StageDir
  #define StageDir "..\bin\stage"
#endif
#define MyAppName "RF4 Companion"
#define MyAppExe "RF4 Companion.exe"

[Setup]
AppId={{6E0C2D4A-8B7F-4C51-9A3E-2F1D7B5C9E40}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Nalathan
AppPublisherURL=https://nalathan.itch.io/rf4-companion
AppSupportURL=https://github.com/Nalathan01/rf4-companion
AppUpdatesURL=https://nalathan.itch.io/rf4-companion
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Nalathan
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup
PrivilegesRequired=lowest
DefaultDirName={code:GetDefaultDir}
DisableProgramGroupPage=yes
DisableDirPage=auto
UsePreviousAppDir=yes
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
OutputBaseFilename=RF4Companion-Setup-{#MyAppVersion}
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
DelData=Also delete your RF4 Companion data (spots, catches, settings, downloaded records)?
german.DelData=Auch deine RF4 Companion Daten löschen (Spots, Fänge, Einstellungen, geladene Rekorde)?
russian.DelData=Удалить также ваши данные RF4 Companion (споты, уловы, настройки, загруженные рекорды)?
french.DelData=Supprimer aussi vos données RF4 Companion (spots, prises, paramètres, records téléchargés) ?
spanish.DelData=¿Eliminar también tus datos de RF4 Companion (spots, capturas, ajustes, récords descargados)?
italian.DelData=Eliminare anche i tuoi dati di RF4 Companion (spot, catture, impostazioni, record scaricati)?
polish.DelData=Usunąć również dane RF4 Companion (miejscówki, połowy, ustawienia, pobrane rekordy)?
brazilianportuguese.DelData=Excluir também seus dados do RF4 Companion (spots, capturas, configurações, recordes baixados)?
dutch.DelData=Ook je RF4 Companion gegevens verwijderen (stekken, vangsten, instellingen, gedownloade records)?
turkish.DelData=RF4 Companion verileriniz de silinsin mi (noktalar, avlar, ayarlar, indirilen rekorlar)?
japanese.DelData=RF4 Companion のデータ (スポット、釣果、設定、ダウンロードした記録) も削除しますか?
korean.DelData=RF4 Companion 데이터 (스팟, 조과, 설정, 다운로드한 기록)도 삭제할까요?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
Type: files; Name: "{app}\RF4Companion.ps1"
Type: files; Name: "{app}\lang.json"
Type: files; Name: "{app}\app.ico"
Type: files; Name: "{app}\version.txt"
Type: files; Name: "{app}\RF4 Companion.exe.config"
Type: files; Name: "{app}\RF4Comp.dll"
Type: filesandordirs; Name: "{app}\data"
Type: filesandordirs; Name: "{app}\Libs"
Type: files; Name: "{app}\Uninstall.ps1"
Type: files; Name: "{app}\Uninstall.exe"
Type: files; Name: "{app}\Uninstall.exe.config"
Type: files; Name: "{app}\Start RF4 Companion.vbs"
Type: files; Name: "{app}\Uninstall.vbs"
Type: files; Name: "{app}\unblocked.txt"

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; AppUserModelID: "Nalathan.RF4Companion"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; WorkingDir: "{app}"; AppUserModelID: "Nalathan.RF4Companion"; Tasks: desktopicon

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\RF4Companion"
Type: files; Name: "{app}\RF4 Companion.exe.old"
Type: files; Name: "{app}\RF4 Companion.exe.new"

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  OldKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\RF4Companion';

function GetDefaultDir(Param: String): String;
var
  Old: String;
begin
  if RegQueryStringValue(HKCU, OldKey, 'InstallLocation', Old) and (Old <> '') then
    Result := Old
  else
    Result := ExpandConstant('{userpf}\{#MyAppName}');
end;

function LangCode: String;
var
  L: String;
begin
  L := ActiveLanguage;
  if L = 'german' then Result := 'de'
  else if L = 'russian' then Result := 'ru'
  else if L = 'french' then Result := 'fr'
  else if L = 'spanish' then Result := 'es'
  else if L = 'italian' then Result := 'it'
  else if L = 'polish' then Result := 'pl'
  else if L = 'brazilianportuguese' then Result := 'pt'
  else if L = 'dutch' then Result := 'nl'
  else if L = 'turkish' then Result := 'tr'
  else if L = 'japanese' then Result := 'ja'
  else if L = 'korean' then Result := 'ko'
  else Result := 'en';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dir, UserFile: String;
begin
  if CurStep = ssPostInstall then
  begin
    RegDeleteKeyIncludingSubkeys(HKCU, OldKey);
    Dir := ExpandConstant('{userappdata}\RF4Companion');
    UserFile := Dir + '\user.json';
    if not FileExists(UserFile) then
    begin
      ForceDirectories(Dir);
      SaveStringToFile(UserFile, '{"lang":"' + LangCode + '"}', False);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if not UninstallSilent then
      if MsgBox(CustomMessage('DelData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\RF4Companion'), True, True, True);
end;
