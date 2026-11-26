<#
.SINOPSIS
  Rescate: fija la contrasena de una cuenta (por omision, admin) directamente en la base de datos.

.DESCRIPCION
  Para cuando no queda ningun Administrador que pueda restablecer la contrasena desde la aplicacion
  (la olvido o la bloqueo el unico). Guarda el hash en el mismo formato que la app (PBKDF2-SHA256),
  activa y desbloquea la cuenta, le asegura el rol Administrador, cierra sus sesiones abiertas y deja
  constancia en la bitacora de auditoria.

  Hay que tener acceso a la base (ser administrador del servidor SQL). Es un recurso de emergencia:
  en el uso normal el Administrador restablece contrasenas desde Administracion > Usuarios y roles.
  La contrasena queda en el historial de la consola si la pasas por parametro: usa el modo interactivo
  (sin -Contrasena) o cambiala al entrar.

.EJEMPLO
  .\scripts\restablecer-admin.ps1 -Conexion "Server=localhost\SQLEXPRESS;Database=DocLibraryV2;Trusted_Connection=True;TrustServerCertificate=True"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Conexion,
    [string]$Usuario = 'admin',
    [string]$Contrasena,
    # Obliga a cambiarla en el siguiente acceso (recomendado si la elige otra persona).
    [switch]$ObligarCambio
)

$ErrorActionPreference = 'Stop'
$Usuario = $Usuario.Trim().ToLowerInvariant()

if (-not $Contrasena) {
    $segura = Read-Host "Nueva contrasena para '$Usuario'" -AsSecureString
    $Contrasena = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura))
}
# Mismas reglas que la aplicacion (ReglasContrasena)
if ($Contrasena.Length -lt 10) { throw 'La contrasena debe tener al menos 10 caracteres.' }
if ($Contrasena.Length -gt 128) { throw 'La contrasena no puede tener mas de 128 caracteres.' }
if (($Contrasena.ToCharArray() | Select-Object -Unique).Count -lt 4) { throw 'La contrasena es demasiado simple.' }
if ($Contrasena.IndexOf($Usuario, [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'La contrasena no puede contener el nombre de usuario.' }

# PBKDF2-SHA256, 310000 iteraciones, sal de 16 bytes, hash de 32 bytes: formato PBKDF2$iter$sal$hash (Base64)
$sal = New-Object byte[] 16
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($sal)
$pbkdf2 = New-Object Security.Cryptography.Rfc2898DeriveBytes($Contrasena, $sal, 310000, [Security.Cryptography.HashAlgorithmName]::SHA256)
$hash = $pbkdf2.GetBytes(32)
$guardado = 'PBKDF2$310000$' + [Convert]::ToBase64String($sal) + '$' + [Convert]::ToBase64String($hash)

$cn = New-Object System.Data.SqlClient.SqlConnection($Conexion)
$cn.Open()
$tx = $cn.BeginTransaction()
try {
    $cmd = $cn.CreateCommand(); $cmd.Transaction = $tx
    $cmd.CommandText = 'SELECT Id, RolesAsignados FROM Usuarios WHERE Identificador = @u'
    [void]$cmd.Parameters.AddWithValue('@u', $Usuario)
    $dt = New-Object System.Data.DataTable; $dt.Load($cmd.ExecuteReader())
    if ($dt.Rows.Count -eq 0) { throw "No existe la cuenta '$Usuario' en esta base de datos." }

    $roles = @(($dt.Rows[0].RolesAsignados -split ',') | Where-Object { $_ })
    if ($roles -notcontains 'Administrador') { $roles += 'Administrador' }
    $rolesTexto = ($roles -join ',')

    $cmd = $cn.CreateCommand(); $cmd.Transaction = $tx
    $cmd.CommandText = @'
UPDATE Usuarios SET HashContrasena = @h, DebeCambiarContrasena = @c, IntentosFallidos = 0, BloqueadoHastaUtc = NULL,
    Activo = 1, SelloSeguridad = NEWID(), RolesAsignados = @r
WHERE Identificador = @u;
INSERT INTO Auditoria (FechaUtc, Usuario, Accion, Entidad, EntidadId, Comentario)
VALUES (SYSUTCDATETIME(), N'SISTEMA', N'Usuario.RestablecerContrasenaManual', N'Usuario', @u,
        N'Contrasena fijada con scripts/restablecer-admin.ps1 por quien tiene acceso a la base de datos. No se registra la contrasena.');
'@
    [void]$cmd.Parameters.AddWithValue('@h', $guardado)
    [void]$cmd.Parameters.AddWithValue('@c', [bool]$ObligarCambio)
    [void]$cmd.Parameters.AddWithValue('@r', $rolesTexto)
    [void]$cmd.Parameters.AddWithValue('@u', $Usuario)
    [void]$cmd.ExecuteNonQuery()
    $tx.Commit()
}
catch { $tx.Rollback(); throw }
finally { $cn.Close() }

Write-Host "Listo: la cuenta '$Usuario' quedo activa, desbloqueada y con la contrasena indicada." -ForegroundColor Green
if ($ObligarCambio) { Write-Host 'Debera cambiarla en su siguiente acceso.' } else { Write-Host 'No se le pedira cambiarla (usa -ObligarCambio si prefieres lo contrario).' }
