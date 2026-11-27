<#
.SINOPSIS
  Administracion diaria del SGD en el servidor: estado, iniciar, detener, reiniciar, log y respaldar.

.DESCRIPCION
  Un solo punto de entrada para no recordar comandos sueltos. Ejecutar en PowerShell COMO ADMINISTRADOR.

    .\sgd.ps1 estado       Resumen: tareas, respuesta de la app, puerto, ultimo respaldo, espacio en disco
    .\sgd.ps1 iniciar      Activa las tareas y arranca la app (espera a que responda)
    .\sgd.ps1 detener      Detiene la app Y pausa el vigilante (queda apagada hasta que uses "iniciar")
    .\sgd.ps1 reiniciar    Detiene y vuelve a arrancar la app (sin que el vigilante intervenga)
    .\sgd.ps1 log [n]      Ultimas n lineas del registro de la app (por omision 40)
    .\sgd.ps1 respaldar    Lanza un respaldo ahora y muestra el resultado

  Trabaja solo con la tarea del SGD y el proceso que corre desde <Destino>\app: no toca otras aplicaciones del servidor.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [ValidateSet('estado', 'iniciar', 'detener', 'reiniciar', 'log', 'respaldar', 'ayuda')] [string]$Accion = 'ayuda',
    [Parameter(Position = 1)] [int]$Lineas = 40,
    [int]$Puerto = 8001,
    [string]$Destino = 'C:\Apps\DocLibrary-V2',
    [string]$CarpetaRespaldos = 'C:\Respaldos\DocLibrary',
    [string]$TareaApp = 'DocLibrary-V2',
    [string]$TareaVigilante = 'DocLibrary-Vigilante',
    [string]$TareaRespaldo = 'DocLibrary-Respaldo'
)

$ErrorActionPreference = 'Stop'
$app = Join-Path $Destino 'app'
$log = Join-Path $Destino 'logs\app.log'
$bloqueo = Join-Path $Destino 'mantenimiento.lock'

function Titulo($t) { Write-Host "`n== $t" -ForegroundColor Cyan }
function Bien($t) { Write-Host "   OK  $t" -ForegroundColor Green }
function Mal($t) { Write-Host "   !!  $t" -ForegroundColor Red }
function Aviso($t) { Write-Host "   ..  $t" -ForegroundColor Yellow }

function Exigir-Admin {
    $esAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $esAdmin) { throw 'Abre PowerShell como Administrador (clic derecho > Ejecutar como administrador).' }
}

function Sano {
    try { return (Invoke-WebRequest -Uri "http://localhost:$Puerto/health" -UseBasicParsing -TimeoutSec 8).StatusCode -eq 200 } catch { return $false }
}

function Esperar-App([int]$segundos = 90) {
    for ($i = 0; $i -lt ($segundos / 3); $i++) { if (Sano) { return $true }; Start-Sleep -Seconds 3 }
    return $false
}

# Termina SOLO el proceso dotnet que corre desde <Destino>\app (la otra aplicacion del servidor no se toca).
function Matar-App {
    $procesos = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object { $_.CommandLine -like "*$app*" }
    foreach ($p in $procesos) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    # Respaldo: si la linea de comando no se pudo leer, se busca por el puerto
    $c = Get-NetTCPConnection -LocalPort $Puerto -State Listen -ErrorAction SilentlyContinue
    if ($c) { Stop-Process -Id $c[0].OwningProcess -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
}

function Puerto-Libre { -not (Get-NetTCPConnection -LocalPort $Puerto -State Listen -ErrorAction SilentlyContinue) }

switch ($Accion) {

    'estado' {
        Titulo 'Tareas programadas'
        foreach ($t in $TareaApp, $TareaVigilante, $TareaRespaldo) {
            $tarea = Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue
            if (-not $tarea) { Mal "$t : NO existe (ejecuta programar-tareas.ps1 / desplegar.ps1)"; continue }
            $info = Get-ScheduledTaskInfo -TaskName $t
            $linea = "{0,-22} estado: {1,-9} ultima ejecucion: {2:yyyy-MM-dd HH:mm}  resultado: {3}" -f $t, $tarea.State, $info.LastRunTime, $info.LastTaskResult
            if ($tarea.State -eq 'Disabled') { Aviso "$linea  (DESACTIVADA)" } else { Bien $linea }
        }

        Titulo "Aplicacion (puerto $Puerto)"
        if (Sano) { Bien "Responde en http://localhost:$Puerto/health" } else { Mal "NO responde en http://localhost:$Puerto/health" }
        $c = Get-NetTCPConnection -LocalPort $Puerto -State Listen -ErrorAction SilentlyContinue
        if ($c) { Bien "Escuchando (PID $($c[0].OwningProcess))" } else { Mal 'Nadie escucha en el puerto' }
        if (Test-Path $bloqueo) { Aviso "Hay un mantenimiento.lock ($((Get-Item $bloqueo).LastWriteTime)): el vigilante no actua mientras exista (max. 45 min)" }

        Titulo 'Respaldos'
        $ultimo = Get-ChildItem $CarpetaRespaldos -Filter 'bd_*.bak' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($ultimo) {
            $horas = [int]((Get-Date) - $ultimo.LastWriteTime).TotalHours
            $texto = "Ultimo: $($ultimo.Name) ($([math]::Round($ultimo.Length / 1MB, 1)) MB, hace $horas h)"
            if ($horas -gt 30) { Mal "$texto  <- mas de 30 h sin respaldo" } else { Bien $texto }
        } else { Mal "No hay respaldos en $CarpetaRespaldos" }

        Titulo 'Disco'
        $unidad = (Split-Path $Destino -Qualifier)
        $d = Get-PSDrive -Name $unidad.TrimEnd(':') -ErrorAction SilentlyContinue
        if ($d) {
            $libreGB = [math]::Round($d.Free / 1GB, 1)
            if ($libreGB -lt 5) { Mal "Libre en ${unidad}: $libreGB GB  <- poco espacio" } else { Bien "Libre en ${unidad}: $libreGB GB" }
        }

        Titulo 'Ultimos mensajes del vigilante'
        $vig = Join-Path $Destino 'logs\vigilante.log'
        if (Test-Path $vig) { Get-Content $vig -Tail 3 | ForEach-Object { Write-Host "   $_" } } else { Write-Host '   (sin eventos: nunca tuvo que reiniciar la app)' }
    }

    'iniciar' {
        Exigir-Admin
        Remove-Item $bloqueo -Force -ErrorAction SilentlyContinue
        Enable-ScheduledTask -TaskName $TareaApp | Out-Null
        Enable-ScheduledTask -TaskName $TareaVigilante | Out-Null
        if (Sano) { Bien 'La app ya estaba respondiendo.' }
        else {
            Start-ScheduledTask -TaskName $TareaApp
            if (Esperar-App) { Bien "La app responde en http://localhost:$Puerto" } else { Mal "No respondio en 90 s. Revisa:  .\sgd.ps1 log"; exit 1 }
        }
    }

    'detener' {
        Exigir-Admin
        Disable-ScheduledTask -TaskName $TareaVigilante | Out-Null     # si no, el vigilante la levantaria otra vez
        Stop-ScheduledTask -TaskName $TareaApp -ErrorAction SilentlyContinue
        Matar-App
        if (Puerto-Libre) { Bien "App detenida y vigilante pausado. Para volver a encenderla:  .\sgd.ps1 iniciar" }
        else { Mal "El puerto $Puerto sigue ocupado. Revisa quien lo usa con:  Get-NetTCPConnection -LocalPort $Puerto -State Listen"; exit 1 }
    }

    'reiniciar' {
        Exigir-Admin
        Set-Content -Path $bloqueo -Value (Get-Date)                   # el vigilante no interviene durante el reinicio
        try {
            Stop-ScheduledTask -TaskName $TareaApp -ErrorAction SilentlyContinue
            Matar-App
            Start-ScheduledTask -TaskName $TareaApp
            if (Esperar-App) { Bien "App reiniciada y respondiendo en http://localhost:$Puerto" } else { Mal "No respondio en 90 s. Revisa:  .\sgd.ps1 log"; exit 1 }
        }
        finally { Remove-Item $bloqueo -Force -ErrorAction SilentlyContinue }
    }

    'log' {
        if (Test-Path $log) { Get-Content $log -Tail $Lineas } else { Mal "No existe $log" }
    }

    'respaldar' {
        Exigir-Admin
        Start-ScheduledTask -TaskName $TareaRespaldo
        for ($i = 0; $i -lt 150 -and (Get-ScheduledTask -TaskName $TareaRespaldo).State -eq 'Running'; $i++) { Start-Sleep -Seconds 2 }
        $r = (Get-ScheduledTaskInfo -TaskName $TareaRespaldo).LastTaskResult
        Get-Content (Join-Path $CarpetaRespaldos 'respaldos.log') -Tail 5
        if ($r -eq 0) { Bien 'Respaldo correcto' } else { Mal "El respaldo termino con codigo $r"; exit 1 }
    }

    default {
        Write-Host @'

 SGD - administracion en el servidor (PowerShell como Administrador)

   .\sgd.ps1 estado       Resumen: tareas, respuesta de la app, puerto, ultimo respaldo, disco
   .\sgd.ps1 iniciar      Activa las tareas y arranca la app (espera a que responda)
   .\sgd.ps1 detener      Detiene la app y pausa el vigilante (queda apagada hasta "iniciar")
   .\sgd.ps1 reiniciar    Detiene y vuelve a arrancar la app
   .\sgd.ps1 log [n]      Ultimas n lineas del registro de la app (por omision 40)
   .\sgd.ps1 respaldar    Lanza un respaldo ahora y muestra el resultado

 Solo toca la tarea del SGD y el proceso de C:\Apps\DocLibrary-V2\app; la otra aplicacion del servidor no se toca.
'@
    }
}
