@echo off
rem Build with the .NET Framework compiler that ships with Windows
rem Windows.Media / Windows.Foundation metadata: read the system media (SMTC) play / pause state
rem TMP points to a short path: csc cannot create temp files under very long paths
cd /d "%~dp0"
if not exist "%~dp0tmp" mkdir "%~dp0tmp"
set "TMP=%~dp0tmp"
set "TEMP=%~dp0tmp"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /r:System.Web.Extensions.dll /r:"%WINDIR%\System32\WinMetadata\Windows.Media.winmd" /r:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:claude-wallpaper-bridge.exe bridge.cs finder.cs input.cs
