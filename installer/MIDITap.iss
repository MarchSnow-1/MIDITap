; MIDITap.iss — Inno Setup 安装包脚本
;
; 定位：给**首次安装**的用户一个双击即装的入口。它装出来的目录与便携包**逐文件相同**，
; 因此 scripts/apply-update.ps1 在两种安装方式下都用同一套逻辑工作。
;
; 为什么安装载荷必须等于 stage/MIDITap：
;   自更新要求包内有 MIDITap.exe 与 scripts/apply-update.ps1（见 UpdateStager.RequiredRelativePaths）
;   若安装器漏掉脚本，用户装完后点「重启并更新」会失败，应用会报"找不到 apply-update.ps1"
;   因此这里直接对 stage 目录取全量，而不是另列一份文件清单 —— 两份清单迟早会走散
;
; 为什么装在 {localappdata}\Programs：
;   PrivilegesRequired=lowest 因此不弹 UAC，普通用户即可安装
;   config/ 与 .storage/ 就在 exe 旁边，与便携包一致（见 AGENTS.md §2.4）
;
; 用户数据保护：
;   config\mapping.json 标了 uninsneveruninstall，卸载不会删掉用户改过的配置
;   .storage/ 由应用自己创建，安装器不曾写入，因此卸载也不会碰它
;
; 构建方式（版本与载荷目录都由命令行传入）：
;   ISCC.exe /DAppVersion=2.0.1 /DAssetTag=v2.0.1 /DSourceDir=..\stage\MIDITap installer\MIDITap.iss
;
; MIDITap.iss — Inno Setup script for the installer
;
; Its job: give first-time users a double-click install path. The directory it produces is
; FILE-FOR-FILE identical to the portable package, so scripts/apply-update.ps1 works the same
; way for both installation routes.
;
; Why the payload has to equal stage/MIDITap:
;   Self-update requires MIDITap.exe and scripts/apply-update.ps1 inside the package
;   (see UpdateStager.RequiredRelativePaths)
;   If the installer left the script out, "restart & update" would fail after installing, and the
;   app would report that apply-update.ps1 is missing
;   So the whole stage directory is taken rather than listing files again: two lists drift apart
;
; Why it installs under {localappdata}\Programs:
;   PrivilegesRequired=lowest means no UAC prompt and an ordinary user can install
;   config/ and .storage/ then sit next to the exe exactly as in the portable package (AGENTS.md §2.4)
;
; Protecting user data:
;   config\mapping.json carries uninsneveruninstall, so uninstalling keeps a config the user edited
;   .storage/ is created by the app and never written by this installer, so uninstall does not touch it
;
; How it is built (version and payload directory both come from the command line):
;   ISCC.exe /DAppVersion=2.0.1 /DAssetTag=v2.0.1 /DSourceDir=..\stage\MIDITap installer\MIDITap.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AssetTag
  #define AssetTag AppVersion
#endif
#ifndef SourceDir
  #define SourceDir "..\stage\MIDITap"
#endif

[Setup]
; AppId 一经发布就不能再改：Inno 靠它判断"这是同一个程序的升级"而不是另一份安装
; Never change AppId once released: Inno uses it to tell an upgrade of the same program from a new one
AppId={{9129796D-8738-47EF-92E9-C393AEAAEBC5}
AppName=MIDITap
AppVersion={#AppVersion}
AppVerName=MIDITap {#AppVersion}
AppPublisher=MarchSnow
AppPublisherURL=https://github.com/MarchSnow-1/MIDITap
AppSupportURL=https://github.com/MarchSnow-1/MIDITap/issues

; 装在用户目录，因此无需管理员权限，也就没有 UAC 弹窗
; Installing under the user profile needs no administrator rights, hence no UAC prompt
DefaultDirName={localappdata}\Programs\MIDITap
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
DisableDirPage=no
AllowNoIcons=yes

OutputDir=..\dist
OutputBaseFilename=MIDITap-{#AssetTag}-win-x64-setup
SetupIconFile=..\src\MIDITap.App\Assets\appIcon.ico
UninstallDisplayIcon={app}\MIDITap.exe
UninstallDisplayName=MIDITap

; lzma2/max 换来明显更小的下载体积；发布流水线的时间预算足够
; lzma2/max buys a clearly smaller download; the release pipeline has the time budget for it
Compression=lzma2/max
SolidCompression=yes

; 与 csproj 的 TargetPlatformMinVersion 一致（Windows 10 1809）
; Matches TargetPlatformMinVersion in the csproj (Windows 10 1809)
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; WizardStyle 是 Inno 6.6 才有的指令，旧版本遇到会直接报错
; 因此由构建方探测版本后决定是否传入该宏（见 .github/actions/build-installer）
;
; WizardStyle only exists from Inno 6.6 onward, and an older version rejects it outright
; The build side therefore probes the version and decides whether to pass this define
; See .github/actions/build-installer
#ifdef ModernWizard
; modern 用纯色背景并去掉顶部分隔线；dynamic 跟随 Windows 的浅色/深色设置
; modern gives a flat background and drops the top divider; dynamic follows the Windows light/dark setting
WizardStyle=modern dynamic
#endif

[Languages]
; 只带英文：官方发行版不含中文语言包，硬引用会让编译失败
; 若要中文界面，把 ChineseSimplified.isl 放进仓库并在下面加一条 Name 项
;
; English only: the official distribution ships no Chinese language file and referencing one would
; break the build. To get a Chinese wizard, vendor ChineseSimplified.isl and add another Name entry
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 载荷 = stage/MIDITap 全量，config 单独排除后按下面的规则再放一次
; The payload is the whole stage/MIDITap; config is excluded here and added again by its own rule below
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "config\mapping.json"; Flags: ignoreversion recursesubdirs createallsubdirs

; 默认配置只在**首次**安装时写入（onlyifdoesntexist），且永不随卸载删除
; 覆盖已有的会让用户改过的映射凭空消失
; The example config is written only on a FIRST install (onlyifdoesntexist) and survives uninstall
; Overwriting an existing one would make a user-edited mapping vanish
Source: "{#SourceDir}\config\mapping.json"; DestDir: "{app}\config"; Flags: ignoreversion onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\MIDITap"; Filename: "{app}\MIDITap.exe"
Name: "{autodesktop}\MIDITap"; Filename: "{app}\MIDITap.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MIDITap.exe"; Description: "{cm:LaunchProgram,MIDITap}"; Flags: nowait postinstall skipifsilent
