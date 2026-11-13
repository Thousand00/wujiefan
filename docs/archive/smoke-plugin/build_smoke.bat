@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\VC\Auxiliary\Build\vcvars32.bat" >nul 2>&1
if errorlevel 1 (echo [ERROR] vcvars32 failed & exit /b 1)

set SRC=E:\WORKS\RTSS-0\src\smoke
set OUT=E:\WORKS\RTSS-0\build\smoke
if not exist "%OUT%" mkdir "%OUT%"
cd /d "%OUT%"

cl /nologo /LD /O2 /MT /EHsc /W3 /DUNICODE /D_UNICODE /DWIN32_LEAN_AND_MEAN "%SRC%\SmokePlugin.cpp" /link /DEF:"%SRC%\SmokePlugin.def" /MACHINE:X86 /SUBSYSTEM:WINDOWS /OUT:"%OUT%\SmokePlugin.dll"
if errorlevel 1 (echo [ERROR] cl failed & exit /b 1)
dir /b "%OUT%"
echo [OK] built SmokePlugin.dll
