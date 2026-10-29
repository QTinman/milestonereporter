@echo off
rem Runs Run-TechnicianReport.ps1 without changing the machine's execution policy.
rem Usage: Run-TechnicianReport.cmd -Customer "Acme" -Recorder RS-North
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Run-TechnicianReport.ps1" %*
exit /b %ERRORLEVEL%
