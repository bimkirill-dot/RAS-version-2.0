@echo off
chcp 65001 >nul
setlocal
rem RAS v. 2.0 для Navisworks 2023 — отдельно от Revit (build.cmd / publish.cmd) и от сайта (site.cmd). Логика — tools\Navisworks.ps1.
rem Двойной щелчок — меню; из командной строки: navisworks.cmd build | publish test | publish all | promote | rollback [test] | status | setup
if not "%~1"=="" goto run
echo RAS v. 2.0 для Navisworks — что сделать?
echo   1 - собрать и поставить себе
echo   2 - выпустить тестовой группе
echo   3 - выпустить всем
echo   4 - сборку «теста» — всем
echo   5 - откатить у всех
echo   6 - откатить у тестовой группы
echo   7 - что сейчас выпущено
echo   8 - выложить установку для коллег (только когда изменилась вкладка: кнопки, иконки)
set "N=1"
set /p "N=Ваш выбор [1]: "
set "ARGS=-Action build"
if "%N%"=="2" set "ARGS=-Action publish -Channel test"
if "%N%"=="3" set "ARGS=-Action publish -Channel all"
if "%N%"=="4" set "ARGS=-Action promote"
if "%N%"=="5" set "ARGS=-Action rollback -Channel all"
if "%N%"=="6" set "ARGS=-Action rollback -Channel test"
if "%N%"=="7" set "ARGS=-Action status"
if "%N%"=="8" set "ARGS=-Action setup"
goto go
:run
set "ARGS=-Action %~1"
if not "%~2"=="" set "ARGS=%ARGS% -Channel %~2"
if /i "%~1"=="rollback" if "%~2"=="" set "ARGS=-Action rollback -Channel all"
:go
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Navisworks.ps1" %ARGS%
pause
