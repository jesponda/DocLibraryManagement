# SGD — Guía de operación del servidor

Para quien administra la plataforma: arrancar, detener, reiniciar, recuperar y mantenerla corriendo.
Todo se hace en **PowerShell como Administrador** (clic derecho → *Ejecutar como administrador*), con la cuenta dueña del proceso.

## 0. Cómo está armado

| Qué | Dónde |
|---|---|
| Aplicación publicada | `C:\Apps\DocLibrary-V2\app` (puerto **8001**) |
| Configuración de arranque | `C:\Apps\DocLibrary-V2\iniciar.ps1` (cadena de conexión, rutas) |
| Archivos de documentos | `C:\Apps\DocLibrary-V2\datos\archivos` |
| Claves de sesión | `C:\Apps\DocLibrary-V2\datos\claves` |
| Registros (logs) | `C:\Apps\DocLibrary-V2\logs\app.log` (se reescribe en cada arranque) y `vigilante.log` |
| Scripts de administración | `C:\Apps\DocLibrary-V2\herramientas\` |
| Base de datos | `DocLibraryV2` en `localhost\SQLEXPRESS` |
| Respaldos | `C:\Respaldos\DocLibrary` (`bd_*.bak`, carpeta `archivos`, `respaldos.log`) |
| Código fuente (para actualizar) | `C:\Repos\DocLibraryManagement` (rama `V2`) |

**Tres tareas programadas** lo mantienen vivo (todas corren como `NT AUTHORITY\SYSTEM`):
- `DocLibrary-V2`: arranca la app con el servidor.
- `DocLibrary-Vigilante`: cada 5 min revisa `http://localhost:8001/health`; si no responde (dos veces, 20 s de por medio) la reinicia.
- `DocLibrary-Respaldo`: cada noche a las 23:30 respalda y verifica la base y copia los archivos; guarda 14 días de `.bak`.

La **otra aplicación del servidor (Python, puerto 8000) es independiente**: estos comandos no la tocan.

## 1. El comando de todos los días: `sgd.ps1`

```powershell
cd C:\Apps\DocLibrary-V2\herramientas      # (o C:\Repos\DocLibraryManagement\scripts)

.\sgd.ps1 estado        # resumen: tareas, ¿responde?, puerto, último respaldo, espacio en disco
.\sgd.ps1 iniciar       # activa las tareas y arranca la app (espera a que responda)
.\sgd.ps1 detener       # detiene la app Y pausa el vigilante (queda apagada hasta "iniciar")
.\sgd.ps1 reiniciar     # detiene y arranca la app sin que el vigilante intervenga
.\sgd.ps1 log 60        # últimas 60 líneas del registro de la app
.\sgd.ps1 respaldar     # respaldo ahora mismo, con su resultado
```
Si PowerShell bloquea el script: `powershell -NoProfile -ExecutionPolicy Bypass -File .\sgd.ps1 estado`.

`estado` marca en rojo lo que requiere atención: tarea inexistente o desactivada, app sin responder, **más de 30 h sin respaldo**, menos de 5 GB libres.

## 2. Tareas típicas

**La app no abre para los usuarios**
1. `.\sgd.ps1 estado`. ¿Responde? ¿Escucha en el puerto?
2. Si no responde: `.\sgd.ps1 reiniciar`. Si sigue sin responder: `.\sgd.ps1 log 60` y busca el último error (lo más común: SQL Server detenido, ver §4).
3. Si responde en el servidor pero no desde otras PC: es el firewall o la red. Verifica la regla: `Get-NetFirewallRule -DisplayName 'DocLibrary-V2*' | Select-Object DisplayName, Enabled`.

**Reiniciar el servidor** (no hay nada que hacer a mano): al encender, la tarea `DocLibrary-V2` arranca la app y el vigilante la vigila. Tras el reinicio, confirma con `.\sgd.ps1 estado`. El servicio `MSSQL$SQLEXPRESS` también debe estar `Running` (§4).

**Apagar la app a propósito** (mantenimiento, mudanza): `.\sgd.ps1 detener`. **No uses `Stop-Process` suelto**: el vigilante la levanta de nuevo.
Para encenderla: `.\sgd.ps1 iniciar`.

**Actualizar a una versión nueva** (avisa antes a los usuarios: la app se reinicia ~1 minuto)
```powershell
.\sgd.ps1 respaldar                     # siempre antes de actualizar
cd C:\Repos\DocLibraryManagement
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desplegar.ps1 `
  -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
# si cambiaron los scripts de operación, refréscalos:
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\programar-tareas.ps1 `
  -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True"
```
El despliegue avisa solo al vigilante y aplica las migraciones de la base. No toca los datos ni los archivos.

## 3. Personas y contraseñas

- Altas, roles, **restablecer contraseña**, desactivar: desde la app, *Administración → Usuarios y roles* (guía de Administradores).
- **Cuenta bloqueada** (5 intentos fallidos): se libera sola a los 15 minutos, o con *Restablecer contraseña*.
- **Nadie puede entrar como Administrador** (se olvidó o se bloqueó la única cuenta): rescate directo en la base. Pide la contraseña por la consola, no se queda en el historial:
```powershell
cd C:\Repos\DocLibraryManagement
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\restablecer-admin.ps1 `
  -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True" `
  -Usuario admin -ObligarCambio
```
Deja la cuenta activa, desbloqueada y con rol de Administrador, cierra sus sesiones y lo anota en la bitácora.

## 4. SQL Server

```powershell
Get-Service 'MSSQL$SQLEXPRESS'                          # debe decir Running
Start-Service 'MSSQL$SQLEXPRESS'                        # si está detenido (luego: .\sgd.ps1 reiniciar)
```
- La cuenta que opera necesita ser **sysadmin** de la instancia (lo es `ALLEGION\cdondiego`). Para darle el rol a otra persona, desde una cuenta sysadmin:
```powershell
$cuenta = 'ALLEGION\otrousuario'
$cn = New-Object System.Data.SqlClient.SqlConnection("Server=localhost\SQLEXPRESS;Database=master;Trusted_Connection=True;TrustServerCertificate=True")
$cn.Open(); $cmd = $cn.CreateCommand()
$cmd.CommandText = "IF SUSER_ID(N'$cuenta') IS NULL CREATE LOGIN [$cuenta] FROM WINDOWS; ALTER SERVER ROLE sysadmin ADD MEMBER [$cuenta];"
[void]$cmd.ExecuteNonQuery(); $cn.Close()
```
- **Si se pierde el acceso de todos los sysadmin:** procedimiento de usuario único (requiere ser administrador local de Windows): `Stop-Service 'MSSQL$SQLEXPRESS' -Force; net start 'MSSQL$SQLEXPRESS' /mSQLCMD`, agregar la cuenta con una conexión con `Application Name=SQLCMD`, y luego `Stop-Service` / `Start-Service` normal.
- Express limita cada base a **10 GB**. Revísalo cada mes: `Get-ChildItem 'C:\Program Files\Microsoft SQL Server\MSSQL*.SQLEXPRESS\MSSQL\DATA' -Filter 'DocLibraryV2*' | Select-Object Name, @{n='GB';e={[math]::Round($_.Length/1GB,2)}}`.

## 5. Respaldos y restauración

**Qué se respalda:** la base (`bd_AAAAMMDD_HHMM.bak`, verificado) y los archivos (copia acumulativa en `archivos`). Las dos cosas van juntas.
**Cuánto:** diario a las 23:30, 14 días de `.bak`. La carpeta de archivos no se depura.

> **Copia `C:\Respaldos\DocLibrary` a otro equipo o unidad de red** (semanal como mínimo). Un respaldo en el mismo disco no sirve si el disco falla.

**Restaurar** (por ejemplo, tras un error grave o en otro servidor). La cuenta debe ser sysadmin. **Detén la app antes**:
```powershell
.\sgd.ps1 detener
$ErrorActionPreference = 'Stop'
$bak = (Get-ChildItem C:\Respaldos\DocLibrary -Filter bd_*.bak | Sort-Object LastWriteTime | Select-Object -Last 1).FullName   # o el .bak que elijas
"Restaurando desde: $bak"
icacls C:\Respaldos\DocLibrary /grant 'NT Service\MSSQL$SQLEXPRESS:(OI)(CI)RX' | Out-Null

$cn = New-Object System.Data.SqlClient.SqlConnection("Server=localhost\SQLEXPRESS;Database=master;Trusted_Connection=True;TrustServerCertificate=True")
$cn.Open(); $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 900
$cmd.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(260)), CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(260))"
$r = $cmd.ExecuteReader(); [void]$r.Read(); $datos = $r.GetString(0); $logs = $r.GetString(1); $r.Close()
$cmd.CommandText = "RESTORE FILELISTONLY FROM DISK = N'$bak'"
$dt = New-Object System.Data.DataTable; $dt.Load($cmd.ExecuteReader())
if ($dt.Rows.Count -eq 0) { throw 'El respaldo no devolvió archivos: revisa la ruta.' }
$mover = ($dt.Rows | ForEach-Object {
  $dest = if ($_.Type -eq 'L') { Join-Path $logs 'DocLibraryV2_log.ldf' } else { Join-Path $datos 'DocLibraryV2.mdf' }
  "MOVE N'$($_.LogicalName)' TO N'$dest'" }) -join ', '
$cmd.CommandText = "IF DB_ID(N'DocLibraryV2') IS NOT NULL ALTER DATABASE [DocLibraryV2] SET SINGLE_USER WITH ROLLBACK IMMEDIATE"
[void]$cmd.ExecuteNonQuery()
$cmd.CommandText = "RESTORE DATABASE [DocLibraryV2] FROM DISK = N'$bak' WITH $mover, REPLACE, STATS = 10"
[void]$cmd.ExecuteNonQuery()
$cmd.CommandText = "ALTER DATABASE [DocLibraryV2] SET MULTI_USER"
[void]$cmd.ExecuteNonQuery()
$cmd.CommandText = "SELECT COUNT(*) FROM DocLibraryV2.dbo.Documentos"
"Base restaurada. Documentos en la base: " + $cmd.ExecuteScalar()
$cn.Close()

# archivos de los documentos (deben corresponder al mismo respaldo)
Copy-Item C:\Respaldos\DocLibrary\archivos\* C:\Apps\DocLibrary-V2\datos\archivos -Recurse -Force
.\sgd.ps1 iniciar
```
Después, entra a la app y abre un documento con PDF para comprobar que los archivos aparecen. Si el respaldo viene de una versión **anterior** de la app, corre `desplegar.ps1` para que aplique las migraciones pendientes.

**Probar la restauración** (hazlo una vez antes de confiar en los respaldos): restaura en otro equipo y compara la lista maestra.

## 6. Revisión periódica

| Cuándo | Qué |
|---|---|
| Diario (1 min) | `.\sgd.ps1 estado`: todo en verde, último respaldo < 30 h |
| Semanal | Copiar `C:\Respaldos\DocLibrary` fuera del servidor. Revisar *Reportes* (documentos vencidos) y la *Bitácora* (accesos extraños) |
| Mensual | Espacio en disco y tamaño de la base (límite 10 GB). Actualizaciones de Windows con reinicio programado fuera de horario. Probar restaurar |
| Al cambiar de responsable | Dar `sysadmin` de SQL y administrador local a la nueva cuenta, y crearle su cuenta de Administrador en la app (*Usuarios y roles*) |

## 7. Si algo no cuadra

| Síntoma | Qué mirar |
|---|---|
| `estado` marca la tarea como inexistente | Volver a correr `desplegar.ps1` o `programar-tareas.ps1` |
| `app.log` dice que no puede conectarse a SQL | `Get-Service 'MSSQL$SQLEXPRESS'`; luego `.\sgd.ps1 reiniciar` |
| El respaldo falla con *permission denied* | Volver a correr `programar-tareas.ps1` con una cuenta sysadmin (da a `SYSTEM` el permiso de verificar) |
| El vigilante reinicia la app seguido | `Get-Content C:\Apps\DocLibrary-V2\logs\vigilante.log -Tail 20` y el `app.log` justo después del reinicio |
| Todos reciben "usuario o contraseña incorrectos" | Probar con otra cuenta; si ninguna entra, revisar `app.log`; la base puede no estar disponible |
| Quedó corriendo una instancia de desarrollo | No uses `iniciar-local.bat` en el servidor; si se lanzó, ciérrala con `Stop-Process` por su PID **solo si no es el puerto 8001 ni el 8000** |

Cuando reportes un problema, adjunta la salida de `.\sgd.ps1 estado` y las últimas líneas de `.\sgd.ps1 log 60`.
