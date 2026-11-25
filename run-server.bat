@echo off
chcp 65001 >nul
setlocal EnableExtensions
title Ferrite - сервер

rem ============================================================
rem  Ferrite: автоматическая установка и запуск сервера
rem  1) ставит .NET 10 SDK, если его нет
rem  2) забирает исходники с GitHub (git или zip)
rem  3) определяет внешний IP
rem  4) открывает порт в брандмауэре
rem  5) собирает и запускает сервер
rem ============================================================

set "REPO=https://github.com/mememend/ferrite.git"
set "ZIPURL=https://codeload.github.com/mememend/ferrite/zip/refs/heads/main"
set "APP=%~dp0"
set "SRC=%APP%ferrite-src"
set "PORT=5222"

echo ==================================================
echo    Ferrite - установка и запуск сервера
echo ==================================================
echo.

rem ---------- 1. .NET 10 SDK ----------
echo [1/6] Проверяю .NET 10 SDK
call :find_dotnet
if not errorlevel 1 goto dotnet_ok

echo       не найден, устанавливаю...
where winget >nul 2>&1
if errorlevel 1 goto dotnet_script
winget install --id Microsoft.DotNet.SDK.10 -e --silent --accept-source-agreements --accept-package-agreements
goto dotnet_after

:dotnet_script
echo       winget недоступен, качаю установщик с dot.net
powershell -NoProfile -ExecutionPolicy Bypass -Command "iwr https://dot.net/v1/dotnet-install.ps1 -OutFile \"$env:TEMP\dotnet-install.ps1\"; & \"$env:TEMP\dotnet-install.ps1\" -Channel 10.0 -InstallDir \"$env:LOCALAPPDATA\Microsoft\dotnet\""

:dotnet_after
set "PATH=%LOCALAPPDATA%\Microsoft\dotnet;%ProgramFiles%\dotnet;%PATH%"
call :find_dotnet
if errorlevel 1 (
  echo.
  echo ОШИБКА: .NET 10 SDK не установился.
  echo Поставь вручную: https://dotnet.microsoft.com/download/dotnet/10.0
  goto fail
)

:dotnet_ok
echo       готово

rem ---------- 2. Исходники ----------
echo [2/6] Получаю исходники
if exist "%SRC%\.git" goto update_repo
where git >nul 2>&1
if errorlevel 1 goto download_zip
echo       клонирую репозиторий...
git clone --depth 1 "%REPO%" "%SRC%"
if errorlevel 1 goto download_zip
goto have_src

:update_repo
echo       обновляю существующую копию...
git -C "%SRC%" pull --ff-only
goto have_src

:download_zip
echo       git не найден, качаю архив...
if exist "%APP%ferrite.zip" del /q "%APP%ferrite.zip"
curl -L -o "%APP%ferrite.zip" "%ZIPURL%"
if not exist "%APP%ferrite.zip" powershell -NoProfile -Command "Invoke-WebRequest -Uri '%ZIPURL%' -OutFile '%APP%ferrite.zip'"
if not exist "%APP%ferrite.zip" (
  echo ОШИБКА: не удалось скачать исходники
  goto fail
)
rmdir /s /q "%SRC%" 2>nul
mkdir "%SRC%"
tar -xf "%APP%ferrite.zip" -C "%SRC%" --strip-components=1
if errorlevel 1 (
  echo ОШИБКА: не удалось распаковать архив
  goto fail
)
del /q "%APP%ferrite.zip"

:have_src
echo       исходники в %SRC%

rem ---------- 3. Внешний IP ----------
echo [3/6] Определяю внешний IP
set "PUBIP="
for /f "usebackq delims=" %%i in (`curl -s --max-time 10 https://api.ipify.org`) do set "PUBIP=%%i"
if not "%PUBIP%"=="" goto got_ip
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "(Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10)"`) do set "PUBIP=%%i"
if not "%PUBIP%"=="" goto got_ip
set /p PUBIP=      не определился, введи внешний IP вручную: 

:got_ip
echo       внешний IP: %PUBIP%

rem ---------- 4. Брандмауэр ----------
echo [4/6] Открываю порт %PORT% в брандмауэре
netsh advfirewall firewall add rule name="Ferrite TCP %PORT%" dir=in action=allow protocol=TCP localport=%PORT% >nul 2>&1
netsh advfirewall firewall add rule name="Ferrite call reflector" dir=in action=allow protocol=UDP localport=46625 >nul 2>&1
echo       если правила не добавились - запусти батник от имени администратора

rem ---------- 5. Сборка ----------
echo [5/6] Собираю сервер (первый раз 1-3 минуты)
pushd "%SRC%"
dotnet build Ferrite\Ferrite.csproj -c Release
if errorlevel 1 (
  popd
  echo ОШИБКА: сборка не удалась
  goto fail
)
popd
echo       готово

rem ---------- 6. Запуск ----------
echo [6/6] Запускаю сервер
echo.
echo   Публичный адрес: %PUBIP%:%PORT%
echo   Коды входа будут печататься здесь же:
echo   Verification code is ==^> 12345 for 79991234567
echo.
echo   Если ПК за роутером - пробрось порт %PORT% TCP на этот компьютер,
echo   иначе с телефона не подключиться.
echo   Остановить: Ctrl+C или закрыть окно.
echo.
set "FERRITE_PUBLIC_ADDRESS=%PUBIP%"
set "FERRITE_PUBLIC_PORT=%PORT%"
set "FERRITE_PORT=%PORT%"
cd /d "%SRC%"
dotnet run --project Ferrite\Ferrite.csproj -c Release --no-build
goto end

rem ---------- helpers ----------
:find_dotnet
rem возвращает 0, если есть SDK 10.x
where dotnet >nul 2>&1
if errorlevel 1 exit /b 1
dotnet --list-sdks 2>nul | findstr /r "^10\." >nul
if errorlevel 1 exit /b 1
exit /b 0

:fail
echo.
echo Установка прервана.
pause
exit /b 1

:end
echo.
echo Сервер остановлен.
pause
exit /b 0
