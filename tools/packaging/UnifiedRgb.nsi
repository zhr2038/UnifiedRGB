Unicode true
!include MUI2.nsh
!include x64.nsh
!include LogicLib.nsh
!include WinVer.nsh
!ifndef PAYLOAD
  !error "PAYLOAD is required"
!endif
!ifndef RELEASE_VERSION
  !define RELEASE_VERSION "0.2.0"
!endif
Name "光域 UnifiedRGB"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\UnifiedRgb"
RequestExecutionLevel admin
ManifestSupportedOS all
ManifestDPIAware true
SetCompressor zlib
SetOverwrite on
BrandingText "光域 · 全机箱灯光与屏幕"
VIProductVersion "${RELEASE_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "光域 UnifiedRGB"
VIAddVersionKey /LANG=2052 "FileDescription" "光域 Windows x64 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${RELEASE_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "UnifiedRGB contributors; third-party notices included"
!define MUI_ABORTWARNING
!define MUI_ICON "${ICON}"
!define MUI_UNICON "${ICON}"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "安装完成。请从开始菜单打开“光域 · 设备配置”，确认本机设备后再运行光域。升级会保留现有配置和场景。"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
Var CurrentVersionDir

Function .onInit
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "光域需要 Windows 10/11。" /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "光域需要 Windows 10/11 x64。" /SD IDOK
    Abort
  ${EndIf}
  SetRegView 64
FunctionEnd

Section "光域" Main
  InitPluginsDir
  SetOutPath "$PLUGINSDIR\payload"
  File /r "${PAYLOAD}\*"
  SetOutPath "$PLUGINSDIR\tools\packaging"
  File "Bundle-Lifecycle.ps1"
  SetOutPath "$PLUGINSDIR\tools\installation"
  File "..\installation\UnifiedRgb.Installation.psm1"
  DetailPrint "校验并安装程序；现有界面必须先退出，未应用草稿不会被强制丢弃。"
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\tools\packaging\Bundle-Lifecycle.ps1" -Mode Install -InstallRoot "$INSTDIR" -Source "$PLUGINSDIR\payload" -Receipt "$PLUGINSDIR\current.txt" -NoStart'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "安装未完成（$0）。请检查详细日志，退出光域界面后重试。旧版本和用户数据不会被强制删除。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  FileOpen $0 "$PLUGINSDIR\current.txt" r
  FileReadUTF16LE $0 $CurrentVersionDir
  FileClose $0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  SetShellVarContext current
  CreateDirectory "$SMPROGRAMS\光域"
  CreateShortcut "$SMPROGRAMS\光域\光域.lnk" "$CurrentVersionDir\UnifiedRgb.App.exe"
  CreateShortcut "$SMPROGRAMS\光域\光域 · 设备配置.lnk" "$CurrentVersionDir\UnifiedRgb.Setup.exe"
  CreateShortcut "$SMPROGRAMS\光域\卸载光域.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "DisplayName" "光域 UnifiedRGB"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "DisplayVersion" "${RELEASE_VERSION}"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "Publisher" "UnifiedRGB"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "CurrentVersionDir" "$CurrentVersionDir"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "DisplayIcon" "$CurrentVersionDir\UnifiedRgb.App.exe"
  WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "NoModify" 1
  WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetRegView 64
  ReadRegStr $CurrentVersionDir HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb" "CurrentVersionDir"
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$CurrentVersionDir\Tools\packaging\Bundle-Lifecycle.ps1" -Mode Uninstall -InstallRoot "$INSTDIR"'
  Pop $0
  ${If} $0 != 0
    MessageBox MB_ICONSTOP "卸载未完成（$0）。请退出光域后重试。配置、场景与未知文件将保留。" /SD IDOK
    SetErrorLevel 1
    Abort
  ${EndIf}
  SetShellVarContext current
  Delete "$SMPROGRAMS\光域\光域.lnk"
  Delete "$SMPROGRAMS\光域\光域 · 设备配置.lnk"
  Delete "$SMPROGRAMS\光域\卸载光域.lnk"
  RMDir "$SMPROGRAMS\光域"
  DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\UnifiedRgb"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
