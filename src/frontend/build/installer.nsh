; Puts <install dir>\resources\cli (the bundled `wdc` CLI) on PATH: the user
; PATH for a per-user install, the machine PATH for an all-users install.
; Plain NSIS, no plugins. The install step appends the entry only when no PATH
; segment already equals it, so reinstalls and updates never duplicate it; the
; uninstaller removes every segment equal to it. A PATH too long for NSIS's
; string buffer (reads fail, writes would cut it) is never touched.

!include WinMessages.nsh

; Overridable so a test build can aim at a scratch registry key instead of the real PATH.
!ifndef WDC_PATH_USER
  !define WDC_PATH_USER 'HKCU "Environment"'
!endif
!ifndef WDC_PATH_MACHINE
  !define WDC_PATH_MACHINE 'HKLM "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"'
!endif

!ifdef BUILD_UNINSTALLER
  !define WDC_FN "un."
!else
  !define WDC_FN ""
!endif

; In:  $R0 = PATH, $R1 = entry (no trailing backslash).
; Out: $R2 = "1" when a segment equals the entry (case-insensitive, trailing
;      backslash ignored), else "0"; $R3 = PATH without those segments and
;      without empty ones. $R4-$R9 are preserved.
Function ${WDC_FN}wdcPathScan
  Push $R4
  Push $R5
  Push $R6
  Push $R7
  Push $R8
  Push $R9
  StrCpy $R2 "0"
  StrCpy $R3 ""
  StrCpy $R4 0
  StrCpy $R5 0
  StrLen $R6 $R0
  scan:
    StrCmp $R4 $R6 closeSegment
    StrCpy $R7 $R0 1 $R4
    StrCmp $R7 ";" closeSegment
    IntOp $R4 $R4 + 1
    Goto scan
  closeSegment:
    IntOp $R8 $R4 - $R5
    StrCpy $R8 $R0 $R8 $R5
    StrCpy $R9 $R8
    StrCpy $R7 $R8 1 -1
    StrCmp $R7 "\" 0 compare
    StrCpy $R9 $R8 -1
  compare:
    StrCmp $R9 $R1 0 keep
    StrCpy $R2 "1"
    Goto advance
  keep:
    StrCmp $R8 "" advance
    StrCmp $R3 "" 0 append
    StrCpy $R3 $R8
    Goto advance
  append:
    StrCpy $R3 "$R3;$R8"
  advance:
    StrCmp $R4 $R6 done
    IntOp $R4 $R4 + 1
    StrCpy $R5 $R4
    Goto scan
  done:
  Pop $R9
  Pop $R8
  Pop $R7
  Pop $R6
  Pop $R5
  Pop $R4
FunctionEnd

!ifndef BUILD_UNINSTALLER
  !macro wdcDefineAdd name target
    Function ${name}
      Push $R0
      Push $R1
      Push $R2
      Push $R3
      Push $R4
      ClearErrors
      ReadRegStr $R0 ${target} "Path"
      StrCpy $R1 "$INSTDIR\resources\cli"
      IfErrors 0 scanPath
      ; The read fails when PATH is absent and also when it is longer than the string
      ; buffer. Only the first may be created; rewriting the second would cut PATH.
      StrCpy $R3 0
    enumerate:
      EnumRegValue $R4 ${target} $R3
      StrCmp $R4 "" setEntry
      StrCmp $R4 "Path" tooLong
      IntOp $R3 $R3 + 1
      Goto enumerate
    scanPath:
      Call wdcPathScan
      StrCmp $R2 "1" done
      StrLen $R3 $R0
      StrLen $R4 $R1
      IntOp $R3 $R3 + $R4
      IntOp $R3 $R3 + 2
      IntCmp $R3 ${NSIS_MAX_STRLEN} tooLong 0 tooLong
      StrCmp $R0 "" setEntry
      StrCpy $R2 $R0 1 -1
      StrCmp $R2 ";" 0 withSeparator
      StrCpy $R0 "$R0$R1"
      Goto write
    withSeparator:
      StrCpy $R0 "$R0;$R1"
      Goto write
    setEntry:
      StrCpy $R0 $R1
    write:
      ClearErrors
      WriteRegExpandStr ${target} "Path" $R0
      IfErrors failed
      SendMessage ${HWND_BROADCAST} ${WM_WININICHANGE} 0 "STR:Environment" /TIMEOUT=5000
      DetailPrint "Added $R1 to PATH"
      Goto done
    tooLong:
      DetailPrint "PATH is too long to extend, wdc was not added to it"
      Goto done
    failed:
      DetailPrint "Could not write PATH, wdc was not added to it"
    done:
      Pop $R4
      Pop $R3
      Pop $R2
      Pop $R1
      Pop $R0
    FunctionEnd
  !macroend
  !insertmacro wdcDefineAdd wdcPathAddUser `${WDC_PATH_USER}`
  !insertmacro wdcDefineAdd wdcPathAddMachine `${WDC_PATH_MACHINE}`
!else
  !macro wdcDefineRemove name target
    Function ${name}
      Push $R0
      Push $R1
      Push $R2
      Push $R3
      Push $R4
      ClearErrors
      ReadRegStr $R0 ${target} "Path"
      IfErrors done
      StrCpy $R1 "$INSTDIR\resources\cli"
      Call un.wdcPathScan
      StrCmp $R2 "1" 0 done
      StrCmp $R3 "" 0 write
      DeleteRegValue ${target} "Path"
      Goto notify
    write:
      WriteRegExpandStr ${target} "Path" $R3
    notify:
      SendMessage ${HWND_BROADCAST} ${WM_WININICHANGE} 0 "STR:Environment" /TIMEOUT=5000
    done:
      Pop $R4
      Pop $R3
      Pop $R2
      Pop $R1
      Pop $R0
    FunctionEnd
  !macroend
  !insertmacro wdcDefineRemove un.wdcPathRemoveUser `${WDC_PATH_USER}`
  !insertmacro wdcDefineRemove un.wdcPathRemoveMachine `${WDC_PATH_MACHINE}`
!endif

!macro customInstall
  ${if} $installMode == "all"
    Call wdcPathAddMachine
  ${else}
    Call wdcPathAddUser
  ${endif}
!macroend

!macro customUnInstall
  ${if} $installMode == "all"
    Call un.wdcPathRemoveMachine
  ${else}
    Call un.wdcPathRemoveUser
  ${endif}
!macroend
