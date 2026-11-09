using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocLibraryManagement.Infrastructure.Consulta;

internal sealed class IndexadorService(
    IDbContextFactory<AppDbContext> fabrica,
    IFileStore almacen,
    IExtractorTexto extractor,
    ILogger<IndexadorService> log) : IIndexadorService
{
    public async Task<int> IndexarPendientesAsync(int maximo = 25, CancellationToken ct = default)
    {
        List<int> pendientes;
        await using (var consulta = await fabrica.CreateDbContextAsync(ct))
        {
            // null = nunca se ha intentado leer; "" = se leyó y no tiene texto (no se reintenta).
            pendientes = await consulta.Revisiones.AsNoTracking()
                .Where(r => r.RutaArchivoPdf != null && r.TextoExtraido == null
                            && (r.Estado == EstadoRevision.Vigente || r.Estado == EstadoRevision.Obsoleto || r.Estado == EstadoRevision.Aprobado))
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .Take(maximo)
                .ToListAsync(ct);
        }

        var procesadas = 0;
        foreach (var id in pendientes)
        {
            try
            {
                if (await IndexarAsync(id, ct))
                {
                    procesadas++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "No se pudo indexar el texto de la revisión {RevisionId}", id);
            }
        }

        return procesadas;
    }

    private async Task<bool> IndexarAsync(int revisionId, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.FirstOrDefaultAsync(x => x.Id == revisionId, ct);
        if (r?.RutaArchivoPdf is null || r.TextoExtraido is not null)
        {
            return false;
        }

        ExtraccionTexto extraccion;
        try
        {
            await using var flujo = almacen.Abrir(r.RutaArchivoPdf);
            extraccion = await extractor.ExtraerAsync(flujo, ct);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Archivo ausente: es un problema de integridad que se reporta al descargar; aquí no se marca como leído.
            log.LogWarning("El PDF de la revisión {RevisionId} no está en el almacén; no se puede indexar.", revisionId);
            return false;
        }

        r.TextoExtraido = extraccion.Texto; // "" si no tiene texto o no se pudo leer: queda marcado como ya intentado
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // la revisión cambió mientras se leía; la próxima pasada la retoma
        }
    }
}
