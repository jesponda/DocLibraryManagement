# SGD — Guía rápida para Administradores de la plataforma

Para quienes dan de alta personas, mantienen los catálogos y cuidan que el sistema funcione.
**Dirección:** `http://________:8001` · **Servidor:** `________` · **Carpeta de la app:** `C:\Apps\DocLibrary-V2`

## 1. Primer día

1. Entra con el usuario `admin` y la **contraseña temporal** que mostró el despliegue. El sistema te pide cambiarla.
2. Crea tu **cuenta personal de Administrador** (sección 2) y entra con ella; deja `admin` solo como cuenta de respaldo con contraseña larga guardada en un lugar seguro.
3. **Administración → Cargar catálogo estándar** (una sola vez): 9 áreas, 66 subáreas (unidades) y 20 tipos de documento, con su flujo Revisión → Aprobación y revisión a 12 meses. Repetirlo es seguro: solo agrega lo que falte.
4. Da de alta a las personas del piloto (sección 2).

## 2. Usuarios y roles — *Administración → Usuarios y roles*

**Dar de alta:** *Nuevo usuario* →
- **Nombre de usuario:** minúsculas, números, punto, guion (ej. `jperez`). **No se puede cambiar después.**
- **Nombre completo** y **correo** (opcional; sirve para los avisos si el correo está activado).
- **Roles** (todas las personas son Lector): Autor, Revisor, Aprobador, Auditor, Administrador.
- **Áreas:** obligatorias para un **Autor** (solo podrá crear y editar en ellas).

Al dar de alta se muestra la **contraseña temporal una sola vez**. Entrégala por un medio seguro (en persona o mensaje directo, nunca en un grupo). La persona la cambia al entrar.

| Quiero… | Hago… |
|---|---|
| Cambiar roles, áreas, nombre o correo | **Editar** → guardar. Surte efecto en su siguiente clic. |
| Alguien olvidó su contraseña o quedó bloqueado | **Restablecer contraseña**: genera otra temporal, desbloquea y cierra sus sesiones. |
| Alguien deja de usar el sistema | **Desactivar**: ya no puede entrar. No se borra nada de su historial. Se reactiva con **Activar**. |

**Reglas de protección:** no puedes quitarte tu propio rol de Administrador, desactivarte ni restablecerte la contraseña (para eso, *Contraseña* arriba a la derecha). Siempre debe quedar al menos un Administrador activo.

> **Quién debe tener qué:** pocos Administradores (2–3). Cada Autor, solo en sus áreas. Revisores y Aprobadores no necesitan ser Autores.

## 3. Catálogos — *Administración*

- **Áreas y subáreas:** una subárea es la **unidad** del código del documento (código de 2–10 letras/números, único por área, **inmutable**). Una vez usada en documentos, no la renombres a otra cosa distinta.
- **Tipos de documento:** código (ej. `F`, `PRO`), nombre y meses entre revisiones (12 por omisión). Cada área ofrece solo los tipos que le asocies.
- **Flujos de aprobación:** pasos de cada tipo (Revisión, Aprobación). La persona concreta se elige al enviar cada documento.
- **Dueño de área:** quien aprueba las revisiones menores y decide los «revisado sin cambios». Defínelo en cada área que use esas revisiones.

## 4. Seguimiento del día a día

- **Reportes:** documentos **vencidos y por vencer**, y tareas pendientes por persona. Úsalo cada semana. Se exporta a Excel/CSV.
- **Bitácora:** quién hizo qué y cuándo (altas de usuario, cambios de rol, inicios de sesión, bloqueos, aprobaciones). No se puede editar ni borrar y **nunca contiene contraseñas**.
- **Cambiar el responsable** de un documento si su dueño se va.
- **Aprobación aún no vigente:** puedes cambiar su fecha o anularla (vuelve a borrador) antes de que entre en vigor.
- **Cola de correo:** muestra si el correo está **desactivado** (valor actual del piloto). Con el correo desactivado no hay avisos por email; los estados «Vencido» sí se ven en pantalla.

## 5. Operación del servidor (PowerShell como Administrador)

```powershell
# ¿Está corriendo?
Get-ScheduledTask DocLibrary-V2 | Select-Object TaskName, State
Invoke-WebRequest http://localhost:8001/health -UseBasicParsing | Select-Object StatusCode

# Ver el registro (errores, arranque)
Get-Content C:\Apps\DocLibrary-V2\logs\app.log -Tail 40

# Detener / iniciar / reiniciar
Stop-ScheduledTask  DocLibrary-V2
Start-ScheduledTask DocLibrary-V2
```

**Actualizar a una versión nueva** (avisa antes a las personas: la app se reinicia ~1 minuto):
```powershell
cd C:\Users\jesponda\source\repos\DocLibraryManagement
git pull
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\desplegar.ps1 `
  -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=true"
```
No toca la base de datos de datos ni los archivos. **Haz un respaldo antes** (sección 6). Al reiniciar, la contraseña temporal del log se borra.

## 6. Respaldos — lo más importante

Hay **dos cosas** que respaldar, y se restauran juntas:
1. La **base de datos** (`DocLibraryV2`).
2. La carpeta de **archivos**: `C:\Apps\DocLibrary-V2\datos\archivos` (los PDF y fuentes).

```powershell
# Preparación (una vez): carpeta y permiso para el servicio de SQL Server
New-Item -ItemType Directory -Force C:\Respaldos\DocLibrary | Out-Null
icacls C:\Respaldos\DocLibrary /grant 'NT Service\MSSQL$SQLEXPRESS:(OI)(CI)M' | Out-Null

# Respaldo (base + archivos)
$fecha = Get-Date -Format yyyyMMdd_HHmm
$cn = New-Object System.Data.SqlClient.SqlConnection("Server=localhost\SQLEXPRESS;Database=master;Trusted_Connection=True;TrustServerCertificate=True")
$cn.Open()
$cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 900
$cmd.CommandText = "BACKUP DATABASE [DocLibraryV2] TO DISK = N'C:\Respaldos\DocLibrary\bd_$fecha.bak' WITH INIT, CHECKSUM"
[void]$cmd.ExecuteNonQuery()
$cmd.CommandText = "RESTORE VERIFYONLY FROM DISK = N'C:\Respaldos\DocLibrary\bd_$fecha.bak' WITH CHECKSUM"
[void]$cmd.ExecuteNonQuery(); $cn.Close()
Copy-Item C:\Apps\DocLibrary-V2\datos\archivos "C:\Respaldos\DocLibrary\archivos_$fecha" -Recurse
```
- Express **no** admite compresión de respaldos; los archivos `.bak` pesan lo que pesa la base.
- **Copia la carpeta `C:\Respaldos\DocLibrary` a otro equipo o unidad de red.** Un respaldo en el mismo disco no sirve si el disco falla.
- Frecuencia sugerida: **diario** y antes de cada actualización. Pídele a IT programarlo (tarea programada) cuando termine el piloto.
- **Prueba restaurar** en otro equipo al menos una vez antes de la entrega: la lista maestra debe coincidir.

## 7. Qué NO hacer

- No borres archivos de `datos\archivos` ni registros de la base a mano: los documentos publicados tienen hash de integridad y evidencia de auditoría.
- No compartas la cuenta `admin`. Cada Administrador usa la suya.
- No cambies el nombre de usuario ni la unidad (subárea) de un documento ya creado: no se permite a propósito (forman parte del código).
- No reinicies el servidor en horas de uso sin avisar.

## 8. Cuándo escalar

Anota **qué hacías, la pantalla, el usuario y la hora**, adjunta las últimas líneas del registro (`app.log`) y repórtalo. Casos que siempre se escalan: la app no abre, error al iniciar sesión de **todos**, o un documento vigente que desapareció de la lista maestra.
