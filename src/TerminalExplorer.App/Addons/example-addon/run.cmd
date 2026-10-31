@echo off
rem  ASCII only: cmd.exe reads .cmd files in the console OEM codepage.
rem  Non-ASCII text here would be mangled into invalid commands.

rem  Terminal Explorer passes what the "arguments" field in addon.json asks for.
rem  With the default template that is:
rem      %1 - the folder currently open in the explorer
rem      %2 - the selected item, or an empty string when nothing is selected

setlocal
chcp 65001 > nul

echo.
echo   Example addon
echo   =============
echo.
echo   Current folder : %~1
echo   Selected item  : %~2
echo   Addon folder   : %~dp0
echo.
echo   Replace this script with whatever your addon should do.
echo   Anything runnable works: .exe, .cmd, .bat, .ps1 - name it in "entry".
echo.

pause
