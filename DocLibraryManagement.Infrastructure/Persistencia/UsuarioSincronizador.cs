using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Persistencia;

internal sealed class UsuarioSincronizador(AppDbContext db, TimeProvider reloj) : IUsuarioSincronizador
{
    public async Task<Usuario> SincronizarAsync(
        string identificador,
        string nombreVisible,
        string? correo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identificador);
        var ahora = reloj.GetUtcNow().UtcDateTime;

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
        await db.SaveChangesAsync(cancellationToken);
        return usuario;
    }
}
