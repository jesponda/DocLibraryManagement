@echo off
rem ============================================================================
rem  Sistema de Gestion Documental - ejecutar en local para pruebas rapidas
rem
rem  Uso:
rem    iniciar-local.bat            Inicia la aplicacion solo en ESTE equipo (localhost)
rem    iniciar-local.bat red        Inicia aceptando conexiones de OTROS equipos de la red
rem    iniciar-local.bat pruebas    Ejecuta las pruebas automatizadas
rem    iniciar-local.bat ayuda      Muestra esta ayuda
rem
rem  Variables opcionales (se definen antes de ejecutar, p. ej.  set PUERTO=5280):
rem    PUERTO         Puerto a usar. Por defecto 5273.
rem    SIN_NAVEGADOR  Si existe, no abre el navegador al iniciar.
rem    SIN_PAUSA      Si existe, no espera una tecla al terminar al hacer doble clic.
rem
rem  Siempre arranca en entorno Development: crea y migra la base de datos de
rem  desarrollo sola y muestra la pantalla de usuarios simulados (sin contrasena).
rem ============================================================================
setlocal EnableExtensions
cd /d "%~dp0"

set "SALIDA=0"

rem Si se ejecuto con doble clic, la ventana se cerraria sola: se pausa al final.
set "DOBLECLIC=0"
echo %cmdcmdline% | find /i "/c" >nul && set "DOBLECLIC=1"
if defined SIN_PAUSA set "DOBLECLIC=0"

if "%PUERTO%"=="" set "PUERTO=5273"
set "MODO=%~1"

if "%MODO%"=="" goto :local
if /i "%MODO%"=="local" goto :local
if /i "%MODO%"=="red" goto :red
if /i "%MODO%"=="pruebas" goto :pruebas
if /i "%MODO%"=="ayuda" goto :ayuda
if /i "%MODO%"=="-h" goto :ayuda
if /i "%MODO%"=="/?" goto :ayuda

echo [ERROR] Opcion desconocida: %MODO%
echo.
set "SALIDA=1"
goto :ayuda

rem ----------------------------------------------------------------------------
:local
call :verificar
if errorlevel 1 goto :fallo
call :puerto_libre
if errorlevel 1 goto :fallo

set "ASPNETCORE_ENVIRONMENT=Development"
echo.
echo Iniciando en http://localhost:%PUERTO%   (Ctrl+C para detener)
echo Entra con un usuario simulado; "Cambiar usuario" arriba a la derecha prueba otro rol.
echo.
call :abrir_navegador
dotnet run --project DocLibraryManagement --no-launch-profile -- --urls "http://localhost:%PUERTO%"
set "SALIDA=%ERRORLEVEL%"
goto :fin

rem ----------------------------------------------------------------------------
:red
call :verificar
if errorlevel 1 goto :fallo
call :puerto_libre
if errorlevel 1 goto :fallo

echo.
echo ADVERTENCIA: esto expone la aplicacion a OTROS equipos de la red SIN contrasena.
echo En modo de desarrollo cualquiera que llegue al puerto puede entrar como Administrador.
echo Usalo solo para pruebas y limita el firewall a la IP del otro equipo.
echo.
set "RESP="
set /p "RESP=Continuar? (S/N): "
if /i not "%RESP%"=="S" goto :cancelado

echo.
echo Direcciones de este equipo para conectarse desde otro:
powershell -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.InterfaceAlias -notmatch 'vEthernet|Loopback|VirtualBox|VMware' } | ForEach-Object { '    http://' + $_.IPAddress + ':%PUERTO%   (' + $_.InterfaceAlias + ')' }"
echo.
echo Si el otro equipo no conecta, abre el puerto SOLO para su IP (PowerShell como administrador):
echo   New-NetFirewallRule -DisplayName "DocLibrary prueba %PUERTO%" -Direction Inbound -Protocol TCP -LocalPort %PUERTO% -Action Allow -Profile Any -RemoteAddress ^<IP-DEL-OTRO-EQUIPO^>
echo Y cierralo al terminar:
echo   Remove-NetFirewallRule -DisplayName "DocLibrary prueba %PUERTO%"
echo.

set "ASPNETCORE_ENVIRONMENT=Development"
echo Iniciando en http://0.0.0.0:%PUERTO%   (Ctrl+C para detener)
echo.
call :abrir_navegador
dotnet run --project DocLibraryManagement --no-launch-profile -- --urls "http://0.0.0.0:%PUERTO%"
set "SALIDA=%ERRORLEVEL%"
goto :fin

rem ----------------------------------------------------------------------------
:pruebas
call :verificar
if errorlevel 1 goto :fallo

echo.
echo Ejecutando las pruebas automatizadas. Necesitan SQL Server LocalDB.
echo Se compilan en una carpeta aparte, asi que funcionan aunque la aplicacion este corriendo.
echo La primera vez tras un descanso puede tardar varios minutos (LocalDB despierta).
echo.
dotnet test DocLibraryManagement.Tests "-p:OutDir=%TEMP%\dl-build\\"
set "SALIDA=%ERRORLEVEL%"
goto :fin

rem ----------------------------------------------------------------------------
:ayuda
echo.
echo  Sistema de Gestion Documental - ejecucion local
echo.
echo    iniciar-local.bat            Inicia la aplicacion solo en este equipo (localhost)
echo    iniciar-local.bat red        Inicia aceptando conexiones de otros equipos de la red
echo    iniciar-local.bat pruebas    Ejecuta las pruebas automatizadas
echo    iniciar-local.bat ayuda      Muestra esta ayuda
echo.
echo  Variables opcionales:  PUERTO (por defecto 5273), SIN_NAVEGADOR, SIN_PAUSA
echo  Ejemplo:  set PUERTO=5280 ^&^& iniciar-local.bat
echo.
echo  Para cerrar la aplicacion: Ctrl+C en esta ventana.
goto :fin

rem ----------------------------------------------------------------------------
:cancelado
echo Cancelado. No se inicio nada.
goto :fin

:fallo
set "SALIDA=1"
goto :fin

:fin
if "%DOBLECLIC%"=="1" (
    echo.
    pause
)
exit /b %SALIDA%

rem ============================================================================
rem  Subrutinas
rem ============================================================================

:verificar
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] No se encontro dotnet. Instala el SDK de .NET 10 desde https://dot.net
    exit /b 1
)
if not exist "DocLibraryManagement\DocLibraryManagement.csproj" (
    echo [ERROR] No se encontro DocLibraryManagement\DocLibraryManagement.csproj
    echo         Este archivo debe estar en la carpeta raiz de la solucion.
    exit /b 1
)
for /f "delims=" %%v in ('dotnet --version 2^>nul') do set "VERSION=%%v"
echo SDK de .NET: %VERSION%
exit /b 0

:puerto_libre
rem Con espacio despues del puerto, para no confundir :5273 con :52730.
netstat -ano | findstr /R /C:":%PUERTO% .*LISTENING" >nul
if not errorlevel 1 (
    echo.
    echo [AVISO] El puerto %PUERTO% ya esta en uso. Quiza ya hay una instancia corriendo.
    echo         Detenla con Ctrl+C en su ventana, o usa otro puerto:  set PUERTO=5280
    exit /b 1
)
exit /b 0

:abrir_navegador
rem Espera a que la aplicacion responda y entonces abre el navegador. Corre en segundo plano.
if defined SIN_NAVEGADOR exit /b 0
start "" /b powershell -NoProfile -Command "for ($i = 0; $i -lt 180; $i++) { try { Invoke-WebRequest 'http://127.0.0.1:%PUERTO%/health' -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process 'http://localhost:%PUERTO%'; break } catch { Start-Sleep -Seconds 1 } }" >nul 2>&1
exit /b 0
