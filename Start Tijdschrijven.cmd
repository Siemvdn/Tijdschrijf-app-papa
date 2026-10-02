@echo off
REM Start de Tijdschrijven-app in je standaardbrowser.
REM Er draait een klein lokaal serventje zodat "Koppel JSON" rechtstreeks
REM naar tijdschrijven.json kan schrijven. Sluit het PowerShell-venster om te stoppen.
cd /d "%~dp0"
start "Tijdschrijven server" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0serve.ps1" -Port 8765
REM even wachten tot de server luistert, daarna browser openen
powershell -NoProfile -Command "Start-Sleep -Milliseconds 900"
start "" http://localhost:8765/
