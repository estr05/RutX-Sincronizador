@echo off
chcp 65001 >nul
title RUTX - Publicar ejecutables
cd /d "%~dp0"

echo ============================================================
echo   RUTX - Generador de ejecutables
echo ============================================================
echo.

set "EXTRA="
if /i "%~1"=="selfcontained" (
    set "EXTRA=-r win-x64 --self-contained true"
    echo   Modo: SELF-CONTAINED (incluye .NET, ~150 MB)
) else (
    echo   Modo: ESTANDAR (requiere .NET Runtime en la PC destino, ~37 MB)
)
echo   Para la otra version:  publicar.bat selfcontained
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo   ERROR: no se encontro 'dotnet'. Instala el .NET SDK 10 y reintenta.
    pause
    exit /b 1
)

if not exist Sincronizador mkdir Sincronizador

echo   [1/2] Publicando Rutx.Sincronizador.exe ...
dotnet publish Rutx.Sincronizador.csproj -c Release %EXTRA% -o Sincronizador
if errorlevel 1 goto :error

echo   [2/2] Publicando Rutx.Sincronizador.Admin.exe ...
dotnet publish Rutx.Sincronizador.Admin\Rutx.Sincronizador.Admin.csproj -c Release %EXTRA% -o Sincronizador
if errorlevel 1 goto :error

echo.
echo   Limpiando archivos de depuracion (*.pdb) del paquete...
del /q "Sincronizador\*.pdb" >nul 2>nul

echo.
echo ============================================================
echo   LISTO. Tus ejecutables estan en:
echo     %~dp0Sincronizador
echo ============================================================
echo     - Rutx.Sincronizador.exe
echo     - Rutx.Sincronizador.Admin.exe
echo     - wwwroot\admin.html    (panel web)
echo ============================================================
echo.
pause
explorer "%~dp0Sincronizador"
exit /b 0

:error
echo.
echo   ERROR: fallo la publicacion. Revisa el mensaje de arriba.
pause
exit /b 1
