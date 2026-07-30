@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-winui.ps1"
if errorlevel 1 pause
