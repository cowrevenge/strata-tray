@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /win32icon:strata.ico /out:StrataTray.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll StrataTray.cs
if errorlevel 1 (echo BUILD FAILED) else (echo BUILD OK: %~dp0StrataTray.exe)
