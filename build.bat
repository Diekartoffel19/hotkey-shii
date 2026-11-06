@echo off
rem Builds dist\AppSwitch.exe (portable) and dist\AppSwitch-Setup.exe (installer).
cd /d "%~dp0"
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll
if not exist dist mkdir dist
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || goto fail

"%CSC%" /nologo /target:winexe /optimize+ /win32icon:icon.ico %REFS% /out:dist\AppSwitch.exe AppSwitch.cs Version.cs || goto fail
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:icon.ico %REFS% /resource:dist\AppSwitch.exe,AppSwitch.exe /out:dist\AppSwitch-Setup.exe Setup.cs Version.cs || goto fail

echo OK: dist\AppSwitch.exe  dist\AppSwitch-Setup.exe
exit /b 0
:fail
echo BUILD FAILED
exit /b 1
