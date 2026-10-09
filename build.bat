@echo off
cd /d "%~dp0"
set CSC="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
rem 1) generate the cat icon from code
%CSC% /nologo /out:"%TEMP%\PimPidIconGen.exe" tools\IconGen.cs src\Mascot.cs || goto fail
"%TEMP%\PimPidIconGen.exe" PimPid.ico || goto fail
rem 2) compile the app with that icon
%CSC% /nologo /codepage:65001 /target:winexe /optimize /win32icon:PimPid.ico /out:PimPid.exe src\*.cs || goto fail
echo Built PimPid.exe
exit /b 0
:fail
echo BUILD FAILED
exit /b 1
