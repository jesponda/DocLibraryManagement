using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Correo;

internal sealed class UsuariosService(
    IDbContextFactory<AppDbContext> fabrica,
    IOptions<CorreoOptions> opciones,
    TimeProvider reloj) : IUsuariosService
{
    public async Task<IReadOnlyList<UsuarioDto>> ListarAsync(Actor actor, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuarios = await db.Usuarios.AsNoTracking().OrderBy(u => u.NombreVisible).ToListAsync(ct);

        var patron = opciones.Value.PatronCorreo;
        return usuarios.Select(u =>
        {
            var efectivo = ResolutorCorreo.Efectivo(u.Correo, u.Identificador, patron);
            return new UsuarioDto(u.Id, u.Identificador, u.NombreVisible, u.Activo, u.RolesUltimoAcceso.Replace(",", ", "),
                u.Correo, efectivo.Direccion, efectivo.Origen, u.UltimoAccesoUtc);
        }).ToList();
    }

    public async Task EstablecerCorreoAsync(Actor actor, Guid usuarioId, string? correo, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        var nuevo = string.IsNullOrWhiteSpace(correo) ? null : correo.Trim();
        if (nuevo is not null && !ResolutorCorreo.EsDireccionValida(nuevo))
        {
            throw new ReglaNegocioException("Esa no es una dirección de correo válida (por ejemplo: nombre@empresa.com).");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == usuarioId, ct)
            ?? throw new ReglaNegocioException("El usuario no existe.");

        var anterior = usuario.Correo;
        usuario.Correo = nuevo;

        Bitacora.Registrar(db, reloj, actor, "Usuario.EstablecerCorreo", nameof(Usuario), usuario.Identificador,
            anterior: anterior ?? "(ninguno; se aplica la convención)", nuevo: nuevo ?? "(ninguno; se aplica la convención)");

        await db.SaveChangesAsync(ct);
    }

    private static void ExigirAdministrador(Actor actor)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede gestionar los usuarios y sus correos.");
        }
    }
}
