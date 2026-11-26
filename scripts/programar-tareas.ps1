<#
.SINOPSIS
  Registra las tareas programadas del SGD: respaldo nocturno y vigilante. Ejecutar como Administrador.

.DESCRIPCION
  - DocLibrary-Respaldo : todos los dias a las 23:30 (por omision) -> respaldar.ps1
  - DocLibrary-Vigilante: cada 5 minutos -> vigilar.ps1 (reinicia el SGD si /health no responde)
  Copia los scripts a <Destino>\herramientas para que sigan funcionando aunque muevas o borres el repositorio
  (si actualizas el repositorio, vuelve a ejecutar este script para refrescarlos). Corren como NT AUTHORITY\SYSTEM.

.EJEMPLO
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\programar-tareas.ps1 `
    -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True"
#>
[CmdletBinding()]
param(
    [string]$Conexion = 'Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True',
    [string]$Destino = 'C:\Apps\DocLibrary-V2',
    [string]$CarpetaRespaldos = 'C:\Respaldos\DocLibrary',
    [string]$HoraRespaldo = '23:30',
    [int]$DiasRetencion = 14,
    [int]$Puerto = 8001,
    [string]$TareaApp = 'DocLibrary-V2',
    # Lanza un respaldo al terminar, para comprobar que todo funciona.
    [switch]$ProbarRespaldo
)

$ErrorActionPreference = 'Stop'
$esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $esAdmin) { throw 'Abre PowerShell como Administrador.' }
if ($Conexion -notmatch 'Trusted_Connection|Integrated Security') {
    Write-Warning 'La cadena de conexion no usa autenticacion de Windows: guardarla en la tarea deja la contrasena visible para los administradores del servidor.'
}

$herramientas = Join-Path $Destino 'herramientas'
New-Item -ItemType Directory -Force -Path $herramientas, $CarpetaRespaldos | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'respaldar.ps1'), (Join-Path $PSScriptRoot 'vigilar.ps1') -Destination $herramientas -Force

$pwsh = (Get-Command powershell.exe).Source
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest

# ---- respaldo nocturno
$argRespaldo = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $herramientas 'respaldar.ps1')`" -Conexion `"$Conexion`" -Destino `"$CarpetaRespaldos`" -Archivos `"$(Join-Path $Destino 'datos\archivos')`" -DiasRetencion $DiasRetencion"
Register-ScheduledTask -TaskName 'DocLibrary-Respaldo' -Force -Principal $principal `
    -Action (New-ScheduledTaskAction -Execute $pwsh -Argument $argRespaldo) `
    -Trigger (New-ScheduledTaskTrigger -Daily -At $HoraRespaldo) `
    -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 2) -StartWhenAvailable -MultipleInstances IgnoreNew) | Out-Null

# ---- vigilante
$argVigilante = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $herramientas 'vigilar.ps1')`" -Puerto $Puerto -Tarea `"$TareaApp`" -Destino `"$Destino`""
Register-ScheduledTask -TaskName 'DocLibrary-Vigilante' -Force -Principal $principal `
    -Action (New-ScheduledTaskAction -Execute $pwsh -Argument $argVigilante) `
    -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650)) `
    -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 4) -StartWhenAvailable -MultipleInstances IgnoreNew) | Out-Null

Write-Host "`nTareas registradas:" -ForegroundColor Green
Get-ScheduledTask -TaskName 'DocLibrary-*' | Select-Object TaskName, State | Format-Table -AutoSize
Write-Host "Respaldo diario a las $HoraRespaldo en $CarpetaRespaldos (se conservan $DiasRetencion dias de .bak)."
Write-Host "Vigilante cada 5 minutos sobre http://localhost:$Puerto/health (log: $Destino\logs\vigilante.log)."

if ($ProbarRespaldo) {
    Write-Host "`nProbando el respaldo ahora..." -ForegroundColor Cyan
    Start-ScheduledTask -TaskName 'DocLibrary-Respaldo'
    for ($i = 0; $i -lt 60 -and (Get-ScheduledTask -TaskName 'DocLibrary-Respaldo').State -eq 'Running'; $i++) { Start-Sleep -Seconds 2 }
    $r = (Get-ScheduledTaskInfo -TaskName 'DocLibrary-Respaldo').LastTaskResult
    Get-Content (Join-Path $CarpetaRespaldos 'respaldos.log') -Tail 6
    if ($r -ne 0) { Write-Host "El respaldo termino con codigo $r. Revisa el log de arriba." -ForegroundColor Red } else { Write-Host 'Respaldo de prueba correcto.' -ForegroundColor Green }
}
