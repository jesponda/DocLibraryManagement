<#
.SINOPSIS
  Respalda la base de datos del SGD y la carpeta de archivos. Pensado para correr cada noche como tarea programada.

.DESCRIPCION
  1. BACKUP DATABASE (con CHECKSUM) y RESTORE VERIFYONLY -> <Destino>\bd_AAAAMMDD_HHMM.bak
  2. Copia la carpeta de archivos a <Destino>\archivos (acumulativa: agrega lo nuevo y no borra nada; los archivos
     publicados no cambian, asi que la copia siempre es un superconjunto de lo que la base referencia).
     La base se respalda ANTES que los archivos a proposito: nunca queda un registro sin su archivo.
  3. Borra los .bak de mas de <DiasRetencion> dias, solo si los pasos anteriores salieron bien.
  Todo queda en <Destino>\respaldos.log. Termina con codigo 1 si algo falla (la tarea programada lo mostrara como error).

  IMPORTANTE: el respaldo en el mismo disco no protege contra la falla del disco. Copia <Destino> a otro equipo o unidad de red.
#>
[CmdletBinding()]
param(
    [string]$Conexion = 'Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True',
    [string]$Destino = 'C:\Respaldos\DocLibrary',
    [string]$Archivos = 'C:\Apps\DocLibrary-V2\datos\archivos',
    [int]$DiasRetencion = 14
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Destino | Out-Null
$log = Join-Path $Destino 'respaldos.log'

function Registrar($texto) {
    $linea = "{0:yyyy-MM-dd HH:mm:ss}  {1}" -f (Get-Date), $texto
    Add-Content -Path $log -Value $linea -Encoding UTF8
    Write-Host $linea
}

try {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($Conexion)
    $base = $b.InitialCatalog
    $instancia = if ($b.DataSource -match '\\(.+)$') { $Matches[1] } else { 'MSSQLSERVER' }

    # El servicio de SQL Server es quien escribe el .bak: necesita permiso en la carpeta de destino.
    $servicio = if ($instancia -eq 'MSSQLSERVER') { 'NT Service\MSSQLSERVER' } else { "NT Service\MSSQL`$$instancia" }
    try { & icacls $Destino /grant "${servicio}:(OI)(CI)M" 2>&1 | Out-Null } catch { }   # si la cuenta no existe (p. ej. LocalDB), se sigue

    $sello = Get-Date -Format 'yyyyMMdd_HHmm'
    $bak = Join-Path $Destino "bd_$sello.bak"

    # ---- 1. base de datos
    Registrar "Inicio. Base: $base  Destino: $Destino"
    $cn = New-Object System.Data.SqlClient.SqlConnection($Conexion)
    $cn.Open()
    $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 1800
    $cmd.CommandText = "BACKUP DATABASE [$base] TO DISK = N'$bak' WITH INIT, CHECKSUM"      # Express no admite COMPRESSION
    [void]$cmd.ExecuteNonQuery()
    $cmd.CommandText = "RESTORE VERIFYONLY FROM DISK = N'$bak' WITH CHECKSUM"
    [void]$cmd.ExecuteNonQuery()
    $cn.Close()
    $mb = [math]::Round((Get-Item $bak).Length / 1MB, 1)
    Registrar "Base respaldada y verificada: $bak ($mb MB)"

    # ---- 2. archivos
    if (Test-Path $Archivos) {
        $destinoArchivos = Join-Path $Destino 'archivos'
        & robocopy $Archivos $destinoArchivos /E /XO /R:2 /W:5 /NFL /NDL /NP /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy fallo (codigo $LASTEXITCODE) al copiar los archivos." }
        $n = (Get-ChildItem $destinoArchivos -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
        Registrar "Archivos copiados a $destinoArchivos ($n archivos en total)"
    } else {
        Registrar "AVISO: no existe la carpeta de archivos $Archivos (se omitio)."
    }

    # ---- 3. retencion
    $limite = (Get-Date).AddDays(-$DiasRetencion)
    $viejos = Get-ChildItem $Destino -Filter 'bd_*.bak' | Where-Object { $_.LastWriteTime -lt $limite }
    foreach ($v in $viejos) { Remove-Item $v.FullName -Force; Registrar "Retencion: borrado $($v.Name)" }
    Registrar "Terminado correctamente. Respaldos de BD conservados: $((Get-ChildItem $Destino -Filter 'bd_*.bak' | Measure-Object).Count)"
}
catch {
    Registrar "ERROR: $($_.Exception.Message)"
    exit 1
}
