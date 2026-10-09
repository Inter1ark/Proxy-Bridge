!define PRODUCT_NAME "ProxyBridge"
!define PRODUCT_VERSION "3.4.0"
!define PRODUCT_PUBLISHER "ProxyBridge Team"
!define PRODUCT_WEB_SITE "https://www.proxybridge.org"
!define PRODUCT_UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${PRODUCT_NAME}"
!define PRODUCT_UNINST_ROOT_KEY "HKLM"

!include "MUI2.nsh"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "..\output\ProxyBridge-Setup-${PRODUCT_VERSION}.exe"
InstallDir "$PROGRAMFILES64\${PRODUCT_NAME}"
InstallDirRegKey HKLM "${PRODUCT_UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin

; Modern UI Configuration
!define MUI_ABORTWARNING
!define MUI_ICON "..\gui\Assets\logo.ico"
!define MUI_UNICON "..\gui\Assets\logo.ico"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_WELCOMEFINISHPAGE_BITMAP_NOSTRETCH
!define MUI_FINISHPAGE_RUN "$INSTDIR\ProxyBridge.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ProxyBridge"

; Installer Pages
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "..\LICENSE"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

; Uninstaller Pages
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Russian"

Section "MainSection" SEC01
  SetOutPath "$INSTDIR"

  ; Upgrade in place (also used by the in-app auto-update: ProxyBridge-Setup.exe /S /D=<dir>).
  ; Close the running app and release the WinDivert driver before overwriting files.
  nsExec::Exec 'taskkill /F /IM ProxyBridge.exe'
  Pop $0
  nsExec::Exec 'sc stop WinDivert'
  Pop $0
  Sleep 1000

  ; Wait until ProxyBridge.exe is writable (the old process may need a moment to release it), up to ~10 s.
  StrCpy $1 0
  IfFileExists "$INSTDIR\ProxyBridge.exe" 0 exe_unlocked
  exe_wait:
    ClearErrors
    FileOpen $0 "$INSTDIR\ProxyBridge.exe" a
    IfErrors 0 exe_close
    IntOp $1 $1 + 1
    IntCmp $1 20 exe_unlocked
    Sleep 500
    Goto exe_wait
  exe_close:
    FileClose $0
  exe_unlocked:

  SetOverwrite on

  ; Main application files (self-contained build)
  File "..\gui\bin\Release\net9.0-windows\win-x64\publish\ProxyBridge.exe"
  File "..\gui\bin\Release\net9.0-windows\win-x64\publish\ProxyBridgeCore.dll"
  File "..\gui\bin\Release\net9.0-windows\win-x64\publish\WinDivert.dll"

  ; The driver can stay locked by the kernel for a while after "sc stop". The shipped driver is the
  ; same WinDivert 2.2.2 build, so keeping the old copy is fine: try once, skip silently if locked.
  SetOverwrite try
  File "..\gui\bin\Release\net9.0-windows\win-x64\publish\WinDivert64.sys"
  SetOverwrite on

  ; All dependencies (includes .NET Runtime)
  File "..\gui\bin\Release\net9.0-windows\win-x64\publish\*.dll"
  File /nonfatal "..\gui\bin\Release\net9.0-windows\win-x64\publish\*.json"
  File /nonfatal "..\gui\bin\Release\net9.0-windows\win-x64\publish\*.pdb"
  
  ; Localization
  SetOutPath "$INSTDIR\ru"
  File /nonfatal "..\gui\bin\Release\net9.0-windows\win-x64\publish\ru\*.*"
  
  SetOutPath "$INSTDIR\zh"
  File /nonfatal "..\gui\bin\Release\net9.0-windows\win-x64\publish\zh\*.*"
  
  ; Runtime native libraries (if any)
  SetOutPath "$INSTDIR"
  File /nonfatal /r "..\gui\bin\Release\net9.0-windows\win-x64\publish\runtimes"
  
  ; Create shortcuts
  SetOutPath "$INSTDIR"
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortCut "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk" "$INSTDIR\ProxyBridge.exe" "" "$INSTDIR\ProxyBridge.exe" 0
  CreateShortCut "$DESKTOP\${PRODUCT_NAME}.lnk" "$INSTDIR\ProxyBridge.exe" "" "$INSTDIR\ProxyBridge.exe" 0
  
  ; Register application
  WriteRegStr HKLM "Software\${PRODUCT_NAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "Software\${PRODUCT_NAME}" "Version" "${PRODUCT_VERSION}"
  
  ; Очистить старую историю прокси (для чистой установки); silent auto-update keeps user data
  IfSilent +2
  Delete "$APPDATA\ProxyBridge\proxy_history.json"
SectionEnd

Section -Post
  WriteUninstaller "$INSTDIR\uninst.exe"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "UninstallString" "$INSTDIR\uninst.exe"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayIcon" "$INSTDIR\ProxyBridge.exe"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "URLInfoAbout" "${PRODUCT_WEB_SITE}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKLM "${PRODUCT_UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegDWORD HKLM "${PRODUCT_UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${PRODUCT_UNINST_KEY}" "NoRepair" 1
  ; Windows Firewall: the relay receives redirected connections as inbound traffic,
  ; so the app needs an inbound allow rule on every profile (VPN and public Wi-Fi are Public).
  nsExec::Exec 'netsh advfirewall firewall delete rule name="ProxyBridge" program="$INSTDIR\ProxyBridge.exe"'
  nsExec::Exec 'netsh advfirewall firewall add rule name="ProxyBridge" dir=in action=allow program="$INSTDIR\ProxyBridge.exe" protocol=TCP enable=yes profile=any'
SectionEnd

Section Uninstall
  nsExec::Exec 'netsh advfirewall firewall delete rule name="ProxyBridge" program="$INSTDIR\ProxyBridge.exe"'
  ; Stop application if running
  ExecWait 'taskkill /F /IM ProxyBridge.exe' $0
  Sleep 500
  
  ; Remove files
  Delete "$INSTDIR\ProxyBridge.exe"
  Delete "$INSTDIR\ProxyBridgeCore.dll"
  Delete "$INSTDIR\WinDivert.dll"
  Delete "$INSTDIR\WinDivert64.sys"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.json"
  Delete "$INSTDIR\uninst.exe"
  
  ; Remove directories
  RMDir /r "$INSTDIR\runtimes"
  RMDir /r "$INSTDIR\ru"
  RMDir /r "$INSTDIR\zh"
  
  ; Remove user data (settings, history)
  Delete "$APPDATA\ProxyBridge\settings.json"
  Delete "$APPDATA\ProxyBridge\proxy_history.json"
  RMDir "$APPDATA\ProxyBridge"
  
  ; Remove shortcuts
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\${PRODUCT_NAME}.lnk"
  Delete "$DESKTOP\${PRODUCT_NAME}.lnk"
  RMDir "$SMPROGRAMS\${PRODUCT_NAME}"
  
  ; Remove installation directory
  RMDir "$INSTDIR"
  
  ; Remove registry keys
  DeleteRegKey ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}"
  DeleteRegKey HKLM "Software\${PRODUCT_NAME}"
  
  SetAutoClose true
SectionEnd

Function .onInit
  ; Check if already installed
  ReadRegStr $R0 ${PRODUCT_UNINST_ROOT_KEY} "${PRODUCT_UNINST_KEY}" "UninstallString"
  StrCmp $R0 "" done

  ; Silent mode (auto-update): no prompt and no uninstall. Files are overwritten in place,
  ; so user settings and the license in %APPDATA%\ProxyBridge stay untouched.
  IfSilent done

  MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION \
  "${PRODUCT_NAME} is already installed. $\n$\nClick 'OK' to remove the previous version or 'Cancel' to cancel this upgrade." \
  IDOK uninst
  Abort
  
uninst:
  ClearErrors
  ExecWait '$R0 _?=$INSTDIR'
  
done:
FunctionEnd
