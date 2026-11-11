using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Reportes;

/// <summary>
/// Consulta de la bitácora (7.6). Solo lectura: la bitácora es inmutable y este servicio no escribe en ella. Solo
/// Administrador y Auditor.
/// </summary>
internal sealed class AuditoriaService(IDbContextFactory<AppDbContext> fabrica) : IAuditoriaService
{
    private const string Intercalacion = "Latin1_General_100_CI_AI";
    private const string CaracterDeEscape = "\\";

    private static void ExigirAcceso(Actor actor)
    {
        if (!(actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor)))
        {
            throw new AccesoDenegadoException("Solo un Administrador o un Auditor puede consultar la bitácora.");
        }
    }

    public async Task<Pagina<EventoAuditoriaDto>> ConsultarAsync(Actor actor, FiltroAuditoria filtro, int pagina = 1, int tamano = 50, CancellationToken ct = default)
    {
        ExigirAcceso(actor);
        var (numero, tam) = ReglasBusqueda.NormalizarPagina(pagina, tamano, 50);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var consulta = AplicarFiltro(db.Auditoria.AsNoTracking(), filtro);

        var total = await consulta.CountAsync(ct);
        var filas = await consulta.OrderByDescending(e => e.Id).Skip((numero - 1) * tam).Take(tam).ToListAsync(ct);

        return new Pagina<EventoAuditoriaDto>([.. filas.Select(ADto)], numero, tam, total);
    }

    public async Task<IReadOnlyList<EventoAuditoriaDto>> ExportarAsync(Actor actor, FiltroAuditoria filtro, CancellationToken ct = default)
    {
        ExigirAcceso(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var filas = await AplicarFiltro(db.Auditoria.AsNoTracking(), filtro)
            .OrderByDescending(e => e.Id)
            .Take(ReglasBusqueda.MaximoFilasExportacion)
            .ToListAsync(ct);

        return [.. filas.Select(ADto)];
    }

    public async Task<IReadOnlyList<string>> EntidadesAsync(Actor actor, CancellationToken ct = default)
    {
        ExigirAcceso(actor);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.Auditoria.AsNoTracking().Select(e => e.Entidad).Distinct().OrderBy(e => e).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<string>> AccionesAsync(Actor actor, CancellationToken ct = default)
    {
        ExigirAcceso(actor);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.Auditoria.AsNoTracking().Select(e => e.Accion).Distinct().OrderBy(a => a).ToListAsync(ct);
    }

    private static IQueryable<EventoAuditoria> AplicarFiltro(IQueryable<EventoAuditoria> consulta, FiltroAuditoria f)
    {
        if (f.DesdeUtc is { } desde)
        {
            consulta = consulta.Where(e => e.FechaUtc >= desde);
        }

        if (f.HastaExclusivoUtc is { } hasta)
        {
            consulta = consulta.Where(e => e.FechaUtc < hasta);
        }

        // Los textos se buscan literalmente (sin comodines) y sin distinguir mayúsculas ni acentos.
        if (ReglasReporte.LimpiarTexto(f.Usuario, "Usuario") is { } usuario)
        {
            var patron = $"%{ReglasBusqueda.EscaparLike(usuario)}%";
            consulta = consulta.Where(e => EF.Functions.Like(EF.Functions.Collate(e.Usuario, Intercalacion), patron, CaracterDeEscape));
        }

        if (ReglasReporte.LimpiarTexto(f.Accion, "Acción") is { } accion)
        {
            var patron = $"{ReglasBusqueda.EscaparLike(accion)}%"; // prefijo: «Revision.» agrupa todas las de revisiones
            consulta = consulta.Where(e => EF.Functions.Like(EF.Functions.Collate(e.Accion, Intercalacion), patron, CaracterDeEscape));
        }

        if (ReglasReporte.LimpiarTexto(f.Entidad, "Entidad") is { } entidad)
        {
            consulta = consulta.Where(e => e.Entidad == entidad);
        }

        if (ReglasReporte.LimpiarTexto(f.EntidadId, "Identificador") is { } entidadId)
        {
            var patron = $"%{ReglasBusqueda.EscaparLike(entidadId)}%";
            consulta = consulta.Where(e => e.EntidadId != null
                                           && EF.Functions.Like(EF.Functions.Collate(e.EntidadId, Intercalacion), patron, CaracterDeEscape));
        }

        if (ReglasReporte.LimpiarTexto(f.Texto, "Texto") is { } texto)
        {
            var patron = $"%{ReglasBusqueda.EscaparLike(texto)}%";
            consulta = consulta.Where(e =>
                (e.Comentario != null && EF.Functions.Like(EF.Functions.Collate(e.Comentario, Intercalacion), patron, CaracterDeEscape))
                || (e.ValorAnterior != null && EF.Functions.Like(EF.Functions.Collate(e.ValorAnterior, Intercalacion), patron, CaracterDeEscape))
                || (e.ValorNuevo != null && EF.Functions.Like(EF.Functions.Collate(e.ValorNuevo, Intercalacion), patron, CaracterDeEscape)));
        }

        return consulta;
    }

    private static EventoAuditoriaDto ADto(EventoAuditoria e) =>
        new(e.Id, e.FechaUtc, e.Usuario, e.Accion, e.Entidad, e.EntidadId, e.ValorAnterior, e.ValorNuevo, e.Comentario, e.DireccionIp);
}
