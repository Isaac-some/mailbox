; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!ifndef PACKAGE_DIR
  !error "PACKAGE_DIR must point to a complete Windows package."
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE is required."
!endif
!ifndef APP_VERSION
  !error "APP_VERSION is required."
!endif
!ifndef UNINSTALL_FILES
  !error "UNINSTALL_FILES is required."
!endif

Name "邮箱助手 ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\MailAssistant"
InstallDirRegKey HKCU "Software\MailAssistant" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
CRCCheck on
ShowInstDetails nevershow
ShowUninstDetails nevershow
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "邮箱助手"
VIAddVersionKey /LANG=2052 "FileDescription" "邮箱助手完整安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "LegalCopyright" "邮箱助手"

!define MUI_ICON "AppIcon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_TITLE "安装完成"
!define MUI_FINISHPAGE_TEXT "邮箱助手已安装。以后可通过桌面上的“邮箱助手”打开。$\r$\n$\r$\n已有账号和邮件数据会保留。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\邮箱助手.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开邮箱助手"
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 Windows 10 或 Windows 11。"
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 Windows 10 或 Windows 11。"
    Abort
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\MailAssistant") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONINFORMATION "请先退出正在运行的邮箱助手，再打开此安装程序。"
    Abort
  ${EndIf}
FunctionEnd

Section "邮箱助手" MainSection
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PACKAGE_DIR}\*"
  WriteUninstaller "$INSTDIR\卸载邮箱助手.exe"
  CreateShortCut "$DESKTOP\邮箱助手.lnk" "$INSTDIR\邮箱助手.exe"
  CreateDirectory "$SMPROGRAMS\邮箱助手"
  CreateShortCut "$SMPROGRAMS\邮箱助手\邮箱助手.lnk" "$INSTDIR\邮箱助手.exe"
  CreateShortCut "$SMPROGRAMS\邮箱助手\卸载邮箱助手.lnk" "$INSTDIR\卸载邮箱助手.exe"
  WriteRegStr HKCU "Software\MailAssistant" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "DisplayName" "邮箱助手"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "DisplayIcon" "$INSTDIR\邮箱助手.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "UninstallString" '$\"$INSTDIR\卸载邮箱助手.exe$\"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant" "NoRepair" 1
SectionEnd

Function un.onInit
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\MailAssistant") p.r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONINFORMATION "请先退出邮箱助手，再卸载。"
    Abort
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  ; Remove installed files only. User data lives in a separate MailAssistant directory.
  !include "${UNINSTALL_FILES}"
  Delete "$INSTDIR\卸载邮箱助手.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\邮箱助手.lnk"
  Delete "$SMPROGRAMS\邮箱助手\邮箱助手.lnk"
  Delete "$SMPROGRAMS\邮箱助手\卸载邮箱助手.lnk"
  RMDir "$SMPROGRAMS\邮箱助手"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MailAssistant"
  DeleteRegKey HKCU "Software\MailAssistant"
SectionEnd
