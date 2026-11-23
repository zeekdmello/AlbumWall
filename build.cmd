@echo off
rem Builds AlbumWall.scr using the C# compiler that ships with Windows.
setlocal
cd /d "%~dp0"
set NET=C:\Windows\Microsoft.NET\Framework64\v4.0.30319
"%NET%\csc.exe" /nologo /target:winexe /optimize+ /out:AlbumWall.exe ^
  /r:"%NET%\WPF\PresentationCore.dll" ^
  /r:"%NET%\WPF\PresentationFramework.dll" ^
  /r:"%NET%\WPF\WindowsBase.dll" ^
  /r:"%NET%\System.Xaml.dll" ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  /r:System.Core.dll ^
  AlbumWall.cs
if errorlevel 1 exit /b 1
move /y AlbumWall.exe AlbumWall.scr >nul
echo Built AlbumWall.scr
