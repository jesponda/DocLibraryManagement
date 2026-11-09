using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocLibraryManagement.Infrastructure.Documentos;

internal sealed class PublicacionService(
    IDbContextFactory<AppDbContext> fabrica,
    TimeProvider reloj,
    ILogger<PublicacionService> log) : IPublicacionService
{
    public async Task<int> ActivarPendientesAsync(CancellationToken ct = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;

        List<int> pendientes;
        await using (var consulta = await fabrica.CreateDbContextAsync(ct))
        {
            pendientes = await consulta.Revisiones.AsNoTracking()
                .Where(r => r.Estado == EstadoRevision.Aprobado && r.FechaVigenciaUtc != null && r.FechaVigenciaUtc <= ahora)
                .OrderBy(r => r.FechaVigenciaUtc)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        var publicadas = 0;
        foreach (var id in pendientes)
        {
            try
            {
                if (await ActivarAsync(id, ct))
                {
                    publicadas++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Una revisión con problemas no debe impedir que se publiquen las demás; se reintenta en la siguiente pasada.
                log.LogError(ex, "No se pudo activar la vigencia de la revisión {RevisionId}", id);
            }
        }

        return publicadas;
    }

    /// <summary>Cada revisión va en su propio contexto y transacción: el fallo de una no arrastra a las otras.</summary>
    private async Task<bool> ActivarAsync(int revisionId, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.Include(x => x.Documento!).ThenInclude(d => d.TipoDocumento)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct);

        // Otra instancia (o un retiro) pudo adelantarse entre la consulta y ahora.
        if (r is not { Estado: EstadoRevision.Aprobado, FechaVigenciaUtc: { } vigencia } || r.Documento!.Estado != EstadoDocumento.Activo)
        {
            return false;
        }

        try
        {
            // Se usa la fecha PROGRAMADA (lo prometido), no la hora en que el proceso alcanzó a correr.
            await Publicacion.PublicarAsync(db, reloj, Actor.Sistema, r, r.Documento.Codigo, r.Documento.TipoDocumento!.MesesRevision, vigencia, ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
