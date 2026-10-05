; 磁力仪数据采集与分析系统 —— Inno Setup 安装包脚本
;
; 不要直接双击编译，请通过仓库根的 build.ps1 调用：
;   .\build.ps1 -Mode Installer
; 它会传入版本号和 dotnet publish 的输出目录：
;   ISCC /DMyAppVersion=0.4.0 /DMySourceDir=<publish目录> /O<输出目录> MagnetometerSystem.iss
;
; 要求 Inno Setup 6.4 或更高（依赖内置的 ChineseSimplified.isl 与 x64compatible 架构标识）。

#ifndef MyAppVersion
  #error 缺少 /DMyAppVersion=x.y.z —— 请通过 build.ps1 调用
#endif
#ifndef MySourceDir
  #error 缺少 /DMySourceDir=<publish 输出目录> —— 请通过 build.ps1 调用
#endif

#define MyAppName "磁力仪数据采集与分析系统"
#define MyAppShortName "MagnetometerSystem"
#define MyAppPublisher "MagnetometerSystem"
#define MyAppExeName "MagnetometerSystem.App.exe"
#define MyAppUrl "https://gitee.com/yialu/MagnetometerSystem"

[Setup]
; AppId 一经确定永不修改，否则新版本会被当成另一个产品并行安装
AppId={{D2D1D0F8-8B7B-4328-BC0E-17E2102BE7D6}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases

; 装到每用户目录而非 Program Files：
;   1. 程序把运行日志写在自身目录下的 logs\，装进 Program Files 会触发 UAC 虚拟化；
;   2. 应用内自动更新拉起本安装包时无需 UAC 提权，可静默完成。
DefaultDirName={localappdata}\Programs\{#MyAppShortName}
PrivilegesRequired=lowest
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=.
OutputBaseFilename=MagnetometerSystem-v{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\MagnetometerSystem.App\Assets\Magnetometer.ico

UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

; 安装前检测运行中的实例。应用侧在 App.OnStartup 创建同名 Mutex，
; 名字改动必须与 App.xaml.cs 的 SingleInstanceMutexName 保持一致。
AppMutex=MagnetometerSystem.SingleInstance
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#MySourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; 不加 skipifsilent —— 应用内自动更新走 /SILENT，装完需要自动把程序拉起来
Filename: "{app}\{#MyAppExeName}"; \
    Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall

[UninstallDelete]
; 日志和自动更新下载的临时文件属于程序目录的衍生物，卸载时一并清掉
Type: filesandordirs; Name: "{app}\logs"

[Code]
// 测量数据库在 %LOCALAPPDATA%\MagnetometerSystem\，与程序目录分离，
// 因此升级覆盖安装天然不会丢数据。卸载时才询问是否一并删除。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\MagnetometerSystem');
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除测量数据和配置？' + #13#10 + #13#10 +
                DataDir + #13#10 + #13#10 +
                '选择"否"将保留数据，重新安装后可继续使用。',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
