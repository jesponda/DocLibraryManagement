using System.Text.RegularExpressions;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Usuarios;

internal sealed partial class CuentasService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : ICuentasService
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,49}$")]
    private static partial Regex PatronIdentificador();

    public async Task<IReadOnlyList<CuentaDto>> ListarAsync(Actor actor, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuarios = await db.Usuarios.AsNoTracking().OrderBy(u => u.NombreVisible).ToListAsync(ct);
        return usuarios.Select(Mapear).ToList();
    }

    public async Task<ResultadoAlta> CrearAsync(Actor actor, string identificador, DatosCuenta datos, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        var id = NormalizarIdentificador(identificador);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var (nombre, correo, roles, areas) = await ValidarAsync(db, datos, ct);

        if (await db.Usuarios.AnyAsync(u => u.Identificador == id, ct))
        {
            throw new ReglaNegocioException($"Ya existe un usuario «{id}». Elige otro nombre de usuario.");
        }

        var temporal = Contrasenas.GenerarTemporal();
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Identificador = id,
            NombreVisible = nombre,
            Correo = correo,
            FechaCreacionUtc = ahora,
            UltimoAccesoUtc = ahora,
            HashContrasena = Contrasenas.Hashear(temporal),
            DebeCambiarContrasena = true,
        };
        Asignar(usuario, roles, areas);
        db.Usuarios.Add(usuario);

        Bitacora.Registrar(db, reloj, actor, "Usuario.Crear", nameof(Usuario), id,
            nuevo: Resumen(roles, areas), comentario: $"Alta de {nombre}. Contraseña temporal generada (no se registra).");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dos altas simultáneas con el mismo nombre de usuario.
            throw new ReglaNegocioException($"Ya existe un usuario «{id}». Elige otro nombre de usuario.");
        }

        return new ResultadoAlta(usuario.Id, id, temporal);
    }

    public async Task ActualizarAsync(Actor actor, Guid id, DatosCuenta datos, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new ReglaNegocioException("El usuario no existe.");
        var (nombre, correo, roles, areas) = await ValidarAsync(db, datos, ct);

        var esUnoMismo = EsElMismo(actor, usuario);
        if (usuario.Activo && TieneRol(usuario, RolSistema.Administrador) && !roles.Contains(RolSistema.Administrador))
        {
            if (esUnoMismo)
            {
                throw new ReglaNegocioException("No puedes quitarte a ti mismo el rol de Administrador. Pídeselo a otro Administrador.");
            }

            await ExigirOtroAdministradorAsync(db, usuario.Id, ct);
        }

        var anterior = Resumen(RolesDe(usuario), AreasDe(usuario));
        usuario.NombreVisible = nombre;
        usuario.Correo = correo;
        Asignar(usuario, roles, areas);

        Bitacora.Registrar(db, reloj, actor, "Usuario.Actualizar", nameof(Usuario), usuario.Identificador,
            anterior: anterior, nuevo: Resumen(roles, areas));

        await db.SaveChangesAsync(ct);
    }

    public async Task<string> RestablecerContrasenaAsync(Actor actor, Guid id, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new ReglaNegocioException("El usuario no existe.");

        if (EsElMismo(actor, usuario))
        {
            throw new ReglaNegocioException("Tu propia contraseña se cambia desde «Cambiar contraseña», no se restablece.");
        }

        var temporal = Contrasenas.GenerarTemporal();
        usuario.HashContrasena = Contrasenas.Hashear(temporal);
        usuario.DebeCambiarContrasena = true;
        usuario.IntentosFallidos = 0;
        usuario.BloqueadoHastaUtc = null;
        usuario.SelloSeguridad = Guid.NewGuid();

        Bitacora.Registrar(db, reloj, actor, "Usuario.RestablecerContrasena", nameof(Usuario), usuario.Identificador,
            comentario: "Contraseña temporal generada (no se registra). Sus sesiones abiertas se cerraron.");

        await db.SaveChangesAsync(ct);
        return temporal;
    }

    public async Task CambiarEstadoAsync(Actor actor, Guid id, bool activa, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw new ReglaNegocioException("El usuario no existe.");

        if (usuario.Activo == activa)
        {
            return;
        }

        if (!activa)
        {
            if (EsElMismo(actor, usuario))
            {
                throw new ReglaNegocioException("No puedes desactivar tu propia cuenta.");
            }

            if (TieneRol(usuario, RolSistema.Administrador))
            {
                await ExigirOtroAdministradorAsync(db, usuario.Id, ct);
            }
        }

        usuario.Activo = activa;
        usuario.SelloSeguridad = Guid.NewGuid();
        if (activa)
        {
            usuario.IntentosFallidos = 0;
            usuario.BloqueadoHastaUtc = null;
        }

        Bitacora.Registrar(db, reloj, actor, activa ? "Usuario.Activar" : "Usuario.Desactivar", nameof(Usuario), usuario.Identificador);
        await db.SaveChangesAsync(ct);
    }

    // ---------- reglas ----------

    internal static string NormalizarIdentificador(string? identificador)
    {
        var id = (identificador ?? "").Trim().ToLowerInvariant();
        if (!PatronIdentificador().IsMatch(id))
        {
            throw new ReglaNegocioException(
                "El nombre de usuario debe tener de 3 a 50 caracteres: letras minúsculas sin acento, números, punto, guion o guion bajo (por ejemplo: jperez).");
        }

        return id;
    }

    private static async Task<(string Nombre, string? Correo, IReadOnlyList<RolSistema> Roles, IReadOnlyList<string> Areas)> ValidarAsync(
        AppDbContext db, DatosCuenta datos, CancellationToken ct)
    {
        var nombre = (datos.Nombre ?? "").Trim();
        if (nombre.Length == 0 || nombre.Length > 200)
        {
            throw new ReglaNegocioException("Escribe el nombre de la persona (hasta 200 caracteres).");
        }

        var correo = string.IsNullOrWhiteSpace(datos.Correo) ? null : datos.Correo.Trim();
        if (correo is not null && !ResolutorCorreo.EsDireccionValida(correo))
        {
            throw new ReglaNegocioException("Esa no es una dirección de correo válida (por ejemplo: nombre@empresa.com).");
        }

        var roles = datos.Roles.Where(r => r != RolSistema.Lector).Distinct().OrderBy(r => r).ToList();
        if (roles.Any(r => !Enum.IsDefined(r)))
        {
            throw new ReglaNegocioException("Hay un rol que no existe.");
        }

        var codigos = datos.Areas.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var areas = new List<string>();
        if (roles.Contains(RolSistema.Autor))
        {
            if (codigos.Count == 0)
            {
                throw new ReglaNegocioException("Un Autor necesita al menos un área en la que pueda editar.");
            }

            var existentes = await db.Areas.AsNoTracking().Where(a => a.Activa).Select(a => a.Codigo).ToListAsync(ct);
            foreach (var codigo in codigos)
            {
                var canonico = existentes.FirstOrDefault(e => e.Equals(codigo, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ReglaNegocioException($"El área «{codigo}» no existe o está inactiva.");
                areas.Add(canonico);
            }
        }

        // Sin el rol Autor las áreas no significan nada (el Administrador edita en todas): no se guardan.
        return (nombre, correo, roles, areas.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void Asignar(Usuario usuario, IReadOnlyList<RolSistema> roles, IReadOnlyList<string> areas)
    {
        usuario.RolesAsignados = string.Join(',', roles);
        usuario.AreasAsignadas = string.Join(',', areas);

        // La copia «último acceso» alimenta las listas de candidatos al asignar etapas de un flujo:
        // se mantiene al día para que la persona aparezca desde el alta, sin esperar a su primer acceso.
        usuario.RolesUltimoAcceso = string.Join(',', roles.Append(RolSistema.Lector).Distinct().OrderBy(r => r));
        usuario.AreasUltimoAcceso = usuario.AreasAsignadas;
    }

    private static async Task ExigirOtroAdministradorAsync(AppDbContext db, Guid excluido, CancellationToken ct)
    {
        var otros = await db.Usuarios.AsNoTracking()
            .Where(u => u.Id != excluido && u.Activo && u.HashContrasena != null)
            .Select(u => u.RolesAsignados)
            .ToListAsync(ct);

        if (!otros.Any(r => ContieneRol(r, RolSistema.Administrador)))
        {
            throw new ReglaNegocioException("Debe quedar al menos un Administrador activo con contraseña. Asigna ese rol a otra persona primero.");
        }
    }

    private static void ExigirAdministrador(Actor actor)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede gestionar los usuarios.");
        }
    }

    private static bool EsElMismo(Actor actor, Usuario usuario) =>
        string.Equals(actor.Identificador, usuario.Identificador, StringComparison.OrdinalIgnoreCase);

    // ---------- lectura de roles ----------

    internal static IReadOnlyList<RolSistema> RolesDe(Usuario u) => LeerRoles(u.RolesAsignados);

    internal static IReadOnlyList<string> AreasDe(Usuario u) =>
        u.AreasAsignadas.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static IReadOnlyList<RolSistema> LeerRoles(string texto) =>
        texto.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => Enum.TryParse<RolSistema>(t, out var r) ? r : (RolSistema?)null)
            .OfType<RolSistema>()
            .ToList();

    private static bool ContieneRol(string texto, RolSistema rol) => LeerRoles(texto).Contains(rol);

    private static bool TieneRol(Usuario u, RolSistema rol) => ContieneRol(u.RolesAsignados, rol);

    private static string Resumen(IEnumerable<RolSistema> roles, IEnumerable<string> areas)
    {
        var r = string.Join(", ", roles);
        var a = string.Join(", ", areas);
        return $"roles: {(r.Length == 0 ? "(solo Lector)" : r)}; áreas: {(a.Length == 0 ? "(ninguna)" : a)}";
    }

    private CuentaDto Mapear(Usuario u)
    {
        var bloqueada = u.BloqueadoHastaUtc is { } hasta && hasta > reloj.GetUtcNow().UtcDateTime ? hasta : (DateTime?)null;
        return new CuentaDto(u.Id, u.Identificador, u.NombreVisible, u.Correo, u.Activo, RolesDe(u), AreasDe(u),
            u.HashContrasena is not null, u.DebeCambiarContrasena, bloqueada, u.UltimoAccesoUtc);
    }
}
