using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Persistencia;

internal sealed class UsuarioSincronizador(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj)
    : IUsuarioSincronizador
{
    public async Task<Usuario> SincronizarAsync(
        string identificador,
        string nombreVisible,
        string? correo,
        IReadOnlyCollection<RolSistema>? roles = null,
        IReadOnlyCollection<string>? areas = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identificador);
        var ahora = reloj.GetUtcNow().UtcDateTime;
        var rolesTexto = roles is null ? null : string.Join(',', roles.OrderBy(r => r));
        var areasTexto = areas is null ? null : string.Join(',', areas.OrderBy(a => a, StringComparer.OrdinalIgnoreCase));

        await using var db = await fabrica.CreateDbContextAsync(cancellationToken);
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == identificador, cancellationToken);

        if (usuario is null)
        {
            usuario = new Usuario
            {
                Id = Guid.NewGuid(),
                Identificador = identificador,
                NombreVisible = nombreVisible,
                Correo = correo,
                FechaCreacionUtc = ahora,
                UltimoAccesoUtc = ahora,
                RolesUltimoAcceso = rolesTexto ?? "",
                AreasUltimoAcceso = areasTexto ?? "",
            };
            db.Usuarios.Add(usuario);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return usuario;
            }
            catch (DbUpdateException)
            {
                // Dos solicitudes simultáneas del mismo usuario nuevo: gana la primera, esta lo relee.
                db.Entry(usuario).State = EntityState.Detached;
                usuario = await db.Usuarios.FirstAsync(u => u.Identificador == identificador, cancellationToken);
            }
        }

        usuario.NombreVisible = nombreVisible;
        usuario.Correo = correo ?? usuario.Correo;
        usuario.UltimoAccesoUtc = ahora;
        usuario.RolesUltimoAcceso = rolesTexto ?? usuario.RolesUltimoAcceso;
        usuario.AreasUltimoAcceso = areasTexto ?? usuario.AreasUltimoAcceso;
        await db.SaveChangesAsync(cancellationToken);
        return usuario;
    }
}
