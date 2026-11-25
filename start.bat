@echo off
rem ============================================
rem  Ferrite: простой запуск сервера.
rem  Ничего не скачивает и не устанавливает.
rem  Код входа появляется прямо в этом окне:
rem  Verification code is ==> 12345 for +7...
rem  Закрыть окно = остановить сервер.
rem ============================================
chcp 65001 >nul
setlocal EnableExtensions
title Ferrite server
cd /d "%~dp0"

rem Папка с данными сервера (ключи, аккаунты) — рядом с батником
set "FERRITE_DATA_PATH=%~dp0data"
set "FERRITE_PORT=5222"
set "FERRITE_PUBLIC_PORT=24868"

rem Внешний IP: чтобы клиенты знали, куда подключаться.
rem Если поменялся — впиши новый вручную здесь:
set "FERRITE_PUBLIC_ADDRESS=159.223.110.159"

rem Готовый сервер собирается из исходников один раз:
rem   dotnet build Ferrite\Ferrite.csproj -c Release
rem Если exe ещё нет — пробуем собрать (нужен .NET SDK).
if not exist "Ferrite\bin\Release\net10.0\Ferrite.exe" (
    if not exist "Ferrite\bin\Release\net10.0\Ferrite.dll" (
        echo [!] Не найден собранный сервер, пробую собрать...
        dotnet build Ferrite\Ferrite.csproj -c Release
        if errorlevel 1 (
            echo [!] Сборка не удалась. Установи .NET SDK 10 и запусти снова.
            pause
            exit /b 1
        )
    )
)

echo ==============================================
echo   Сервер Ferrite запускается: 0.0.0.0:5222
echo   Публичный адрес: %FERRITE_PUBLIC_ADDRESS%:%FERRITE_PUBLIC_PORT%
echo   Данные: %FERRITE_DATA_PATH%
echo   Код входа появится ниже строкой
echo   "Verification code is ==>"
echo   Остановить: Ctrl+C или закрыть окно
echo ==============================================

if exist "Ferrite\bin\Release\net10.0\Ferrite.exe" (
    "Ferrite\bin\Release\net10.0\Ferrite.exe"
) else (
    dotnet "Ferrite\bin\Release\net10.0\Ferrite.dll"
)
pause
