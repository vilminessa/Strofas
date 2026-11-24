@echo off
chcp 65001 >nul
setlocal EnableExtensions
cd /d "%~dp0"

rem ============================================================
rem  Strofas - консоль управления проектом
rem  debug.bat          - меню
rem  debug.bat 1        - сразу выполнить пункт (для скриптов/CI)
rem  Подсказка: в текстах меню нет спецсимволов cmd (| & >),
rem  а метки только латиницей - так надёжнее с UTF-8.
rem ============================================================

set "scripted="
set "sel="
if not "%~1"=="" (set "scripted=1" & set "sel=%~1")

set "branch=?"
for /f "delims=" %%b in ('git rev-parse --abbrev-ref HEAD 2^>nul') do set "branch=%%b"
set "dirty=0"
for /f %%c in ('git status --porcelain 2^>nul ^| find /c /v ""') do set "dirty=%%c"

set "dbgexe=src\Strofas\bin\Debug\net10.0-windows\Strofas.exe"
set "pubexe=src\Strofas\bin\Release\net10.0-windows\win-x64\publish\Strofas.exe"
set "logfile=%LOCALAPPDATA%\Strofas\strofas.log"

if defined scripted goto dispatch

:menu
cls
echo.
echo  ===============================================================
echo    Strofas - консоль управления проектом
echo  ===============================================================
echo    ветка: %branch%    не закоммичено файлов: %dirty%
echo  ---------------------------------------------------------------
echo    [1] Собрать Debug и запустить меню
echo    [2] Запустить меню без сборки
echo    [3] Запустить редактор
echo    [4] Запуск для скриншота (меню видно в захвате экрана)
echo    [5] Собрать Release
echo    [6] Publish exe (self-contained single-file)
echo  ---------------------------------------------------------------
echo    [7] Остановить процессы Strofas
echo    [8] Лог: последние 40 строк
echo    [B] Открыть папку конфига и лога
echo    [C] Открыть папку publish
echo    [D] Открыть папку проекта
echo  ---------------------------------------------------------------
echo    [9] git: статус и последние коммиты
echo    [A] git: добавить всё, закоммитить, запушить
echo  ---------------------------------------------------------------
echo    [0] Выход
echo.
set "sel="
set /p "sel=Выбор: "
if not defined sel goto menu
if "%sel%"=="0" exit /b 0

:dispatch
if "%sel%"=="1" goto op_build_run
if "%sel%"=="2" goto op_run
if "%sel%"=="3" goto op_editor
if "%sel%"=="4" goto op_shot
if "%sel%"=="5" goto op_build
if "%sel%"=="6" goto op_publish
if "%sel%"=="7" goto op_kill
if "%sel%"=="8" goto op_log
if "%sel%"=="B" goto op_config
if "%sel%"=="b" goto op_config
if "%sel%"=="C" goto op_publish_dir
if "%sel%"=="c" goto op_publish_dir
if "%sel%"=="D" goto op_project
if "%sel%"=="d" goto op_project
if "%sel%"=="9" goto op_git_status
if "%sel%"=="A" goto op_git_push
if "%sel%"=="a" goto op_git_push
if defined scripted (echo Неизвестный пункт: %~1 & exit /b 2)
goto menu

rem ---------------------------------------------------------------
rem  Сборка и запуск
rem ---------------------------------------------------------------

:op_build_run
echo Сборка Debug...
dotnet build Strofas.slnx -c Debug
set "rc=%errorlevel%"
if not "%rc%"=="0" goto after
echo Запуск меню...
start "" "%dbgexe%" --menu
set "rc=0"
goto after

:op_run
if not exist "%dbgexe%" (echo Сборки нет - используйте пункт 1 & set "rc=1" & goto after)
echo Запуск меню без сборки...
start "" "%dbgexe%" --menu
set "rc=0"
goto after

:op_editor
if not exist "%dbgexe%" (echo Сборки нет - используйте пункт 1 & set "rc=1" & goto after)
echo Запуск редактора...
start "" "%dbgexe%" --editor
set "rc=0"
goto after

:op_shot
if not exist "%dbgexe%" (echo Сборки нет - используйте пункт 1 & set "rc=1" & goto after)
echo Запуск меню со STROFAS_NO_EXCLUDE=1 (меню попадает в скриншоты)...
set "STROFAS_NO_EXCLUDE=1"
start "" "%dbgexe%" --menu
set "STROFAS_NO_EXCLUDE="
set "rc=0"
goto after

rem ---------------------------------------------------------------
rem  Сборка релиза
rem ---------------------------------------------------------------

:op_build
echo Сборка Release...
dotnet build Strofas.slnx -c Release
set "rc=%errorlevel%"
goto after

:op_publish
echo Publish exe (self-contained single-file)...
dotnet publish src\Strofas -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
set "rc=%errorlevel%"
if not "%rc%"=="0" goto after
if exist "%pubexe%" (echo Артефакт: %pubexe%) else (echo Артефакт не найден & set "rc=1")
goto after

rem ---------------------------------------------------------------
rem  Процессы, лог, папки
rem ---------------------------------------------------------------

:op_kill
echo Останавливаю Strofas...
taskkill /IM Strofas.exe /F >nul 2>&1
echo Готово.
set "rc=0"
goto after

:op_log
if not exist "%logfile%" (echo Лога ещё нет - запустите приложение & set "rc=1" & goto after)
powershell -NoProfile -Command "Get-Content -Encoding UTF8 -Tail 40 '%logfile%'"
set "rc=%errorlevel%"
goto after

:op_config
if exist "%LOCALAPPDATA%\Strofas" (explorer "%LOCALAPPDATA%\Strofas") else (echo Профиля ещё нет - запустите приложение)
set "rc=0"
goto after

:op_publish_dir
if exist "%~dp0src\Strofas\bin\Release\net10.0-windows\win-x64\publish" (explorer "%~dp0src\Strofas\bin\Release\net10.0-windows\win-x64\publish") else (echo Папки нет - выполните пункт 6)
set "rc=0"
goto after

:op_project
explorer "%~dp0"
set "rc=0"
goto after

rem ---------------------------------------------------------------
rem  Git
rem ---------------------------------------------------------------

:op_git_status
git status -sb
echo ---------------------------------------------------------------
git log --oneline -6
set "rc=%errorlevel%"
goto after

:op_git_push
echo Текущие изменения:
git status -sb
echo.
set "msg="
set /p "msg=Сообщение коммита (пусто = отмена): "
if not defined msg goto menu
echo.
git add -A
git commit -m "%msg%"
set "rc=%errorlevel%"
if not "%rc%"=="0" goto after
git push origin master
set "rc=%errorlevel%"
goto after

rem ---------------------------------------------------------------
rem  Возврат в меню
rem ---------------------------------------------------------------

:after
if defined scripted exit /b %rc%
echo.
echo Код возврата: %rc%
pause
goto menu
