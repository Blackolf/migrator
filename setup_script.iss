[Setup]
AppName=系統遷移與淨化大師
AppVersion=1.0
DefaultDirName={autopf}\SystemMigrator
DefaultGroupName=系統遷移與淨化大師
; 輸出的安裝包名稱與位置
OutputDir=.\installer_output
OutputBaseFilename=SystemMigrator_Setup_v1.0
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
SetupIconFile=compiler:SetupClassicIcon.ico
UninstallDisplayIcon={app}\SystemMigrator.exe

[Languages]
; 使用繁體中文介面
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 指向我們剛才用 dotnet publish 生成的單檔案
Source: ".\publish\Migrator.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\系統遷移與淨化大師"; Filename: "{app}\SystemMigrator.exe"
Name: "{group}\解除安裝系統遷移大師"; Filename: "{uninstallexe}"
Name: "{autodesktop}\系統遷移與淨化大師"; Filename: "{app}\SystemMigrator.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SystemMigrator.exe"; Description: "{cm:LaunchProgram,系統遷移與淨化大師}"; Flags: nowait postinstall skipifsilent