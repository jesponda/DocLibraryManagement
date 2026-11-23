<#
.SINOPSIS
  Publica y deja corriendo el SGD en un servidor Windows, en su propio puerto, sin tocar otras aplicaciones.

.DESCRIPCION
  Desde la raiz del repositorio (ya con `git pull`):
    1. Publica la aplicacion en <Destino>\app (se detiene antes si ya corria).
    2. Crea la base de datos si no existe y aplica las migraciones (dotnet ef).
    3. Escribe <Destino>\iniciar.ps1 con TODA la configuracion (variables solo de ese proceso: no afecta a otras apps).
    4. Abre el puerto en el firewall de Windows.
    5. Registra una tarea programada que arranca la aplicacion con el servidor, la inicia y espera a que responda.
    6. Muestra la contrasena temporal del usuario administrador (se genera al primer arranque).

  Re-ejecutarlo actualiza la aplicacion (no toca la base de datos de datos ni los archivos). Requiere PowerShell como
  Administrador, el SDK de .NET 10 y acceso a SQL Server.

.EJEMPLO
  .\scripts\desplegar.ps1 -Conexion "Server=MISERVIDOR;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True"
#>
[CmdletBinding()]
param(
    [int]$Puerto = 8001,
    [string]$Destino = 'C:\Apps\DocLibrary-V2',
    [string]$Conexion = 'Server=localhost;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true',
    [string]$NombreTarea = 'DocLibrary-V2',
    [string]$AdminUsuario = 'admin',
    [string]$CodigoSitio = 'QRT',
    # Para pruebas: arranca la app como proceso normal, sin tarea programada ni firewall.
    [switch]$SinTarea
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $Destino 'app'
$datos = Join-Path $Destino 'datos'
$logs = Join-Path $Destino 'logs'
$log = Join-Path $logs 'app.log'

function Paso($texto) { Write-Host "`n== $texto" -ForegroundColor Cyan }

$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin -and -not $SinTarea) { throw 'Abre PowerShell como Administrador (hace falta para la tarea programada y el firewall).' }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'No se encontro "dotnet". Instala el SDK de .NET 10 (winget install Microsoft.DotNet.SDK.10), cierra y abre PowerShell como Administrador y vuelve a ejecutar.'
}
if (-not ((dotnet --list-sdks) -match '^10\.')) {
    throw 'Hace falta el SDK completo de .NET 10 (no solo el runtime). Instalalo con: winget install Microsoft.DotNet.SDK.10'
}

# El puerto no debe estar ocupado por OTRA cosa (si es esta misma app, se detiene mas abajo).
$ocupado = Get-NetTCPConnection -LocalPort $Puerto -State Listen -ErrorAction SilentlyContinue
if ($ocupado -and -not (Get-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue) -and -not $SinTarea) {
    throw "El puerto $Puerto ya esta en uso por otro proceso (PID $($ocupado[0].OwningProcess)). Elige otro con -Puerto."
}

# ---------------------------------------------------------------- 1. detener y publicar
Paso '1/6 Deteniendo la version anterior (si existe) y publicando'
$tarea = Get-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue
if ($tarea) { Stop-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue }
# Solo se detiene el proceso que corre desde ESTA carpeta (la otra aplicacion, en el 8000, no se toca).
Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
    Where-Object { $_.CommandLine -like "*$app*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Start-Sleep -Seconds 1 }

New-Item -ItemType Directory -Force -Path $app, $datos, $logs, (Join-Path $datos 'archivos'), (Join-Path $datos 'claves') | Out-Null
Set-Location $repo
dotnet publish DocLibraryManagement -c Release -o $app --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'La publicacion fallo.' }

# ---------------------------------------------------------------- 2. base de datos
Paso '2/6 Creando la base de datos (si no existe) y aplicando migraciones'
if (-not (Get-Command dotnet-ef -ErrorAction SilentlyContinue) -and -not ((dotnet tool list -g) -match 'dotnet-ef')) {
    dotnet tool install --global dotnet-ef --version 10.0.14
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo instalar dotnet-ef.' }
}
$env:DOCLIB_CONNECTION = $Conexion
dotnet ef database update --project DocLibraryManagement.Infrastructure --startup-project DocLibraryManagement.Infrastructure
if ($LASTEXITCODE -ne 0) { throw 'No se pudieron aplicar las migraciones. Revisa la cadena de conexion y los permisos en SQL Server.' }
Remove-Item Env:\DOCLIB_CONNECTION

# ---------------------------------------------------------------- 3. configuracion de arranque
Paso '3/6 Escribiendo la configuracion de arranque'
$inicio = Join-Path $Destino 'iniciar.ps1'
@"
# Generado por desplegar.ps1. Variables SOLO de este proceso: no afectan a otras aplicaciones del servidor.
`$env:ASPNETCORE_ENVIRONMENT = 'Production'
`$env:ASPNETCORE_URLS = 'http://0.0.0.0:$Puerto'
`$env:ConnectionStrings__DefaultConnection = '$($Conexion.Replace("'", "''"))'
`$env:Autenticacion__Modo = 'Local'
`$env:Autenticacion__AdministradorInicial__Usuario = '$AdminUsuario'
`$env:Autenticacion__AdministradorInicial__ContrasenaTemporal = ''   # vacia = se genera y se escribe una vez en el log
`$env:Archivos__RutaBase = '$(Join-Path $datos 'archivos')'
`$env:DataProtection__RutaClaves = '$(Join-Path $datos 'claves')'
`$env:Documentos__CodigoSitio = '$CodigoSitio'
Set-Location '$app'
# El log se reescribe en cada arranque: la contrasena temporal no queda guardada para siempre.
& dotnet '$(Join-Path $app 'DocLibraryManagement.dll')' *> '$log'
"@ | Set-Content -Path $inicio -Encoding UTF8

# La cuenta que corre la app necesita acceso a la base (con Trusted_Connection y SYSTEM, hay que darselo).
$cuenta = if ($SinTarea) { [Security.Principal.WindowsIdentity]::GetCurrent().Name } else { 'NT AUTHORITY\SYSTEM' }
if ($Conexion -match 'Trusted_Connection\s*=\s*(True|Yes|SSPI)|Integrated Security' -and -not $SinTarea) {
    Paso "   Dando acceso a $cuenta en SQL Server"
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($Conexion)
    $maestra = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($Conexion)
    $maestra['Initial Catalog'] = 'master'
    $cn = New-Object System.Data.SqlClient.SqlConnection($maestra.ConnectionString)
    $cn.Open()
    $sql = @"
IF SUSER_ID(N'$cuenta') IS NULL CREATE LOGIN [$cuenta] FROM WINDOWS;
USE [$($b.InitialCatalog)];
IF USER_ID(N'$cuenta') IS NULL CREATE USER [$cuenta] FOR LOGIN [$cuenta];
ALTER ROLE db_owner ADD MEMBER [$cuenta];
"@
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; [void]$cmd.ExecuteNonQuery(); $cn.Close()
}

# ---------------------------------------------------------------- 4. firewall
Paso "4/6 Firewall: puerto $Puerto"
if (-not $SinTarea) {
    $regla = "DocLibrary-V2 (TCP $Puerto)"
    if (-not (Get-NetFirewallRule -DisplayName $regla -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $regla -Direction Inbound -Protocol TCP -LocalPort $Puerto -Action Allow -Profile Domain,Private | Out-Null
    }
} else { Write-Host '   (omitido: -SinTarea)' }

# ---------------------------------------------------------------- 5. arranque
Paso '5/6 Iniciando la aplicacion'
$pwsh = (Get-Command powershell.exe).Source
if ($SinTarea) {
    Start-Process -FilePath $pwsh -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $inicio -WindowStyle Hidden
} else {
    $accion = New-ScheduledTaskAction -Execute $pwsh -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$inicio`""
    $disparador = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $config = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
    Register-ScheduledTask -TaskName $NombreTarea -Action $accion -Trigger $disparador -Principal $principal -Settings $config -Force | Out-Null
    Start-ScheduledTask -TaskName $NombreTarea
}

$arriba = $false
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 2
    try { if ((Invoke-WebRequest -Uri "http://localhost:$Puerto/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $arriba = $true; break } } catch { }
}
if (-not $arriba) {
    Write-Host "`nLa aplicacion no respondio en el puerto $Puerto. Ultimas lineas del log:" -ForegroundColor Red
    if (Test-Path $log) { Get-Content $log -Tail 25 }
    throw 'Arranque fallido.'
}

# ---------------------------------------------------------------- 6. contrasena del administrador
Paso '6/6 Usuario administrador'
$linea = if (Test-Path $log) { Select-String -Path $log -Pattern 'Contrase.a temporal' | Select-Object -Last 1 } else { $null }
$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1).IPAddress

Write-Host "`nListo. Direccion en la red local:  http://$($ip):$Puerto" -ForegroundColor Green
if ($linea) {
    $contrasena = ($linea.Line.Trim() -split '\s+')[-1]
    Write-Host "Usuario:               $AdminUsuario"
    Write-Host "Contrasena temporal:   $contrasena" -ForegroundColor Yellow
    Write-Host 'Se pedira cambiarla al primer acceso. Anotala ahora: se borra del log en el siguiente reinicio.'
} else {
    Write-Host 'No se creo un administrador nuevo: ya existian cuentas en esta base de datos.'
}
Write-Host "Log: $log"
