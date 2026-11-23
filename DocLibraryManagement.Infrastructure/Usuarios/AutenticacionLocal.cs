using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Usuarios;

internal sealed class AutenticacionLocal(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : IAutenticacionLocal
{
    public const int IntentosAntesDeBloquear = 5;
    public static readonly TimeSpan DuracionBloqueo = TimeSpan.FromMinutes(15);

    public async Task<RespuestaInicioSesion> IniciarSesionAsync(
        string identificador, string contrasena, string? direccionIp, CancellationToken ct = default)
    {
        var id = (identificador ?? "").Trim().ToLowerInvariant();
        var ahora = reloj.GetUtcNow().UtcDateTime;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = id.Length == 0 ? null : await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == id, ct);

        // Siempre se hace una verificación completa, exista o no el usuario: la demora no delata quién tiene cuenta.
        var coincide = Contrasenas.Verificar(contrasena ?? "", usuario?.HashContrasena);
        if (usuario is null || !usuario.Activo || usuario.HashContrasena is null)
        {
            return new RespuestaInicioSesion(ResultadoInicioSesion.Rechazado);
        }

        if (usuario.BloqueadoHastaUtc is { } hasta && hasta > ahora)
        {
            return new RespuestaInicioSesion(ResultadoInicioSesion.Bloqueado);
        }

        if (!coincide)
        {
            var bloqueado = RegistrarFallo(db, usuario, ahora, direccionIp);
            await db.SaveChangesAsync(ct);
            return new RespuestaInicioSesion(bloqueado ? ResultadoInicioSesion.Bloqueado : ResultadoInicioSesion.Rechazado);
        }

        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHastaUtc = null;
        usuario.UltimoAccesoUtc = ahora;
        var acceso = Mapear(usuario);
        Bitacora.Registrar(db, reloj, ActorDe(acceso, direccionIp), "Sesion.Inicio", nameof(Usuario), usuario.Identificador);
        await db.SaveChangesAsync(ct);
        return new RespuestaInicioSesion(ResultadoInicioSesion.Correcto, acceso);
    }

    public async Task<AccesoCuenta?> ObtenerAccesoAsync(string identificador, CancellationToken ct = default)
    {
        var id = (identificador ?? "").Trim().ToLowerInvariant();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Identificador == id, ct);
        return usuario is null ? null : Mapear(usuario);
    }

    public async Task<AccesoCuenta> CambiarContrasenaAsync(
        string identificador, string actual, string nueva, string? direccionIp, CancellationToken ct = default)
    {
        var id = (identificador ?? "").Trim().ToLowerInvariant();
        var ahora = reloj.GetUtcNow().UtcDateTime;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == id && u.Activo, ct)
            ?? throw new AccesoDenegadoException("Tu cuenta no está disponible.");

        if (usuario.BloqueadoHastaUtc is { } hasta && hasta > ahora)
        {
            throw new ReglaNegocioException("Demasiados intentos fallidos. Espera unos minutos o pide a un Administrador que restablezca tu contraseña.");
        }

        if (!Contrasenas.Verificar(actual ?? "", usuario.HashContrasena))
        {
            // Quien robara una sesión abierta no puede adivinar la contraseña actual sin límite.
            RegistrarFallo(db, usuario, ahora, direccionIp);
            await db.SaveChangesAsync(ct);
            throw new ReglaNegocioException("La contraseña actual no es correcta.");
        }

        if (nueva == actual)
        {
            throw new ReglaNegocioException("La contraseña nueva debe ser distinta de la actual.");
        }

        if (ReglasContrasena.Validar(nueva, usuario.Identificador) is { } problema)
        {
            throw new ReglaNegocioException(problema);
        }

        usuario.HashContrasena = Contrasenas.Hashear(nueva);
        usuario.DebeCambiarContrasena = false;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHastaUtc = null;
        usuario.SelloSeguridad = Guid.NewGuid(); // cierra las demás sesiones abiertas con la contraseña anterior

        var acceso = Mapear(usuario);
        Bitacora.Registrar(db, reloj, ActorDe(acceso, direccionIp), "Usuario.CambiarContrasena", nameof(Usuario), usuario.Identificador);
        await db.SaveChangesAsync(ct);
        return acceso;
    }

    public async Task<string?> AsegurarAdministradorInicialAsync(
        string identificador, string nombre, string? contrasenaTemporal, CancellationToken ct = default)
    {
        var id = CuentasService.NormalizarIdentificador(identificador);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        if (await db.Usuarios.AnyAsync(u => u.HashContrasena != null, ct))
        {
            return null; // ya hay cuentas con contraseña: no se toca nada
        }

        var temporal = string.IsNullOrWhiteSpace(contrasenaTemporal) ? Contrasenas.GenerarTemporal() : contrasenaTemporal;
        var ahora = reloj.GetUtcNow().UtcDateTime;

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == id, ct);
        if (usuario is null)
        {
            usuario = new Usuario
            {
                Id = Guid.NewGuid(),
                Identificador = id,
                NombreVisible = string.IsNullOrWhiteSpace(nombre) ? id : nombre.Trim(),
                FechaCreacionUtc = ahora,
                UltimoAccesoUtc = ahora,
            };
            db.Usuarios.Add(usuario);
        }

        usuario.Activo = true;
        usuario.HashContrasena = Contrasenas.Hashear(temporal);
        usuario.DebeCambiarContrasena = true;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHastaUtc = null;
        usuario.SelloSeguridad = Guid.NewGuid();
        usuario.RolesAsignados = RolSistema.Administrador.ToString();
        usuario.AreasAsignadas = "";
        usuario.RolesUltimoAcceso = $"{RolSistema.Lector},{RolSistema.Administrador}";

        Bitacora.Registrar(db, reloj, Actor.Sistema, "Usuario.AdministradorInicial", nameof(Usuario), id,
            comentario: "Administrador inicial creado al arrancar (aún no había cuentas con contraseña). Contraseña temporal no registrada.");
        await db.SaveChangesAsync(ct);
        return temporal;
    }

    // ---------- internos ----------

    /// <returns><c>true</c> si este fallo dejó la cuenta bloqueada.</returns>
    private bool RegistrarFallo(AppDbContext db, Usuario usuario, DateTime ahora, string? direccionIp)
    {
        usuario.IntentosFallidos++;
        if (usuario.IntentosFallidos < IntentosAntesDeBloquear)
        {
            return false;
        }

        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHastaUtc = ahora + DuracionBloqueo;
        Bitacora.Registrar(db, reloj, ActorDe(Mapear(usuario), direccionIp), "Sesion.Bloqueo", nameof(Usuario), usuario.Identificador,
            comentario: $"{IntentosAntesDeBloquear} intentos fallidos: bloqueada {DuracionBloqueo.TotalMinutes:0} minutos.");
        return true;
    }

    private static AccesoCuenta Mapear(Usuario u)
    {
        var roles = new HashSet<RolSistema>(CuentasService.RolesDe(u)) { RolSistema.Lector };
        var areas = new HashSet<string>(CuentasService.AreasDe(u), StringComparer.OrdinalIgnoreCase);
        return new AccesoCuenta(u.Id, u.Identificador, u.NombreVisible, u.Activo, u.DebeCambiarContrasena, u.SelloSeguridad, roles, areas);
    }

    private static Actor ActorDe(AccesoCuenta c, string? ip) => new(c.Identificador, c.Roles, c.Areas, ip);
}
