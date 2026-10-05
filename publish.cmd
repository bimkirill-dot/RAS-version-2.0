@echo off
chcp 65001 >nul
setlocal
rem Выпуск новой версии на сервер: publish.cmd test (тестовой группе) или publish.cmd all (всем).
set "CH=%~1"
if not "%CH%"=="" goto run
echo Кому выпустить новую версию?
echo   1 - тестовой группе (канал «тест»)
echo   2 - всем
set "N=1"
set /p "N=Ваш выбор [1]: "
set "CH=test"
if "%N%"=="2" set "CH=all"
:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Release.ps1" -Action publish -Channel %CH%
pause
