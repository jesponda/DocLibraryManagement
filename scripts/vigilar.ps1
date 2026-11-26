<#
.SINOPSIS
  Vigilante del SGD: si la aplicacion no responde, la reinicia. Pensado para correr cada pocos minutos como tarea programada.

.DESCRIPCION
  - Pide http://localhost:<Puerto>/health. Si responde 200, no hace nada.
  - Si no responde, espera 20 s y reintenta (evita reiniciar por un tropiezo momentaneo).
  - Si sigue sin responder: detiene la tarea <Tarea>, termina SOLO el proceso dotnet que corre desde <Destino>\app
    (otras aplicaciones del servidor no se tocan) y la vuelve a iniciar. Espera hasta 90 s a que responda.
  - No actua durante un despliegue o mantenimiento: si existe <Destino>\mantenimiento.lock (menos de 45 min de antiguedad) se salta.
    Para dar mantenimiento a mano sin que el vigilante interfiera:  Set-Content C:\Apps\DocLibrary-V2\mantenimiento.lock 'x'
    y al terminar:  Remove-Item C:\Apps\DocLibrary-V2\mantenimiento.lock
  Registra solo lo importante en <Destino>\logs\vigilante.log.
#>
[CmdletBinding()]
param(
    [int]$Puerto = 8001,
    [string]$Tarea = 'DocLibrary-V2',
    [string]$Destino = 'C:\Apps\DocLibrary-V2'
)

$ErrorActionPreference = 'Stop'
$logs = Join-Path $Destino 'logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null
$log = Join-Path $logs 'vigilante.log'
$lock = Join-Path $Destino 'mantenimiento.lock'

function Registrar($texto) {
    if ((Test-Path $log) -and (Get-Item $log).Length -gt 1MB) { Move-Item $log "$log.anterior" -Force }
    Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $texto) -Encoding UTF8
}

function Sano {
    try { return (Invoke-WebRequest -Uri "http://localhost:$Puerto/health" -UseBasicParsing -TimeoutSec 10).StatusCode -eq 200 }
    catch { return $false }
}

if ((Test-Path $lock) -and ((Get-Date) - (Get-Item $lock).LastWriteTime).TotalMinutes -lt 45) { exit 0 }   # mantenimiento en curso
if (Sano) { exit 0 }

Start-Sleep -Seconds 20
if (Sano) { exit 0 }

Registrar "El SGD no responde en el puerto $Puerto. Reiniciando la tarea '$Tarea'."
try {
    Stop-ScheduledTask -TaskName $Tarea -ErrorAction SilentlyContinue
    $app = Join-Path $Destino 'app'
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -like "*$app*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
    Start-ScheduledTask -TaskName $Tarea

    for ($i = 0; $i -lt 18; $i++) {
        Start-Sleep -Seconds 5
        if (Sano) { Registrar 'El SGD volvio a responder.'; exit 0 }
    }
    Registrar "ERROR: el SGD sigue sin responder tras reiniciarlo. Revisa $Destino\logs\app.log."
    exit 1
}
catch {
    Registrar "ERROR al reiniciar: $($_.Exception.Message)"
    exit 1
}
