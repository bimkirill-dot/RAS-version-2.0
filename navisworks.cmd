@echo off
chcp 65001 >nul
rem RAS v. 2.0 для Navisworks: navisworks.cmd [build] — собрать, проверить, поставить себе (tools\Navisworks.ps1).
rem Отдельно от Revit (build.cmd / publish.cmd) и от сайта (site.cmd).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Navisworks.ps1" %*
pause
