@echo off
REM Bouwt dist\Tijdschrijven.exe met de .NET Framework-compiler die in elke Windows zit.
REM Gebruik: tools\build_exe.cmd   (icoon opnieuw maken: python tools\make_icon.py)
cd /d "%~dp0.."
if not exist dist mkdir dist
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:dist\Tijdschrijven.exe /win32icon:app\app.ico /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll app\Tijdschrijven.cs
if errorlevel 1 exit /b 1
copy /y index.html dist\ >nul
copy /y logo.png dist\ >nul
echo Klaar: dist\Tijdschrijven.exe  (zet tijdschrijven.json in dezelfde map)
