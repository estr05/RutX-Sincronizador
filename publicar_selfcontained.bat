@echo off
chcp 65001 >nul
title RUTX - Publicar version SELF-CONTAINED
echo.
echo   Version SELF-CONTAINED (todo adentro, ~150 MB)
echo   Para PCs del cliente que NO tienen .NET Runtime instalado.
echo.
call "%~dp0publicar.bat" selfcontained
