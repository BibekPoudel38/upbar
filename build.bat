@echo off
rem Builds Upbar with the C# compiler that ships with Windows (.NET Framework 4). No installs needed.
rem The icon (upbar.ico) is drawn by the app itself: Upbar.exe --make-icon upbar.ico
set F=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
set W=C:\Windows\System32\WinMetadata
set ICON=
if exist "%~dp0upbar.ico" set ICON=/win32icon:"%~dp0upbar.ico"
"%F%\csc.exe" /nologo /optimize /target:winexe /out:"%~dp0Upbar.exe" %ICON% ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
  /r:"%F%\System.Runtime.dll" /r:"%F%\System.Runtime.InteropServices.WindowsRuntime.dll" /r:"%F%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%W%\Windows.Foundation.winmd" /r:"%W%\Windows.Devices.winmd" /r:"%W%\Windows.Networking.winmd" ^
  /r:"%W%\Windows.Media.winmd" /r:"%W%\Windows.Storage.winmd" /r:"%W%\Windows.Security.winmd" ^
  /r:"%W%\Windows.UI.winmd" /r:"%W%\Windows.ApplicationModel.winmd" /r:"%W%\Windows.Data.winmd" ^
  "%~dp0src\*.cs"
