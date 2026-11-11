using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Documentos;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Reportes;

internal sealed class ReportesService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : IReportesService
{
    /// <summary>Misma intercalación que la lista maestra: sin distinguir mayúsculas ni acentos, sea cual sea la de la BD.</summary>
    private const string Intercalacion = "Latin1_General_100_CI_AI";

    private const string CaracterDeEscape = "\\";

    // ---------- Alcance ----------

    /// <summary>
    /// De qué áreas puede ver reportes el actor. <c>null</c> = todas (Administrador y Auditor). Un Autor ve las áreas donde
    /// edita; un Aprobador, las de las que es dueño; quien tenga ambos roles, la unión. Sin ninguna, no hay reporte.
    /// </summary>
    private static async Task<List<string>?> AreasVisiblesAsync(AppDbContext db, Actor actor, CancellationToken ct)
    {
        if (actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor))
        {
            return null;
        }

        var codigos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (actor.TieneRol(RolSistema.Autor))
        {
            foreach (var area in actor.Areas)
            {
                codigos.Add(area.ToUpperInvariant());
            }
        }

        if (actor.TieneRol(RolSistema.Aprobador))
        {
            var propias = await db.Areas.AsNoTracking()
                .Where(a => a.Dueno != null && a.Dueno.Identificador == actor.Identificador)
                .Select(a => a.Codigo)
                .ToListAsync(ct);
            foreach (var codigo in propias)
            {
                codigos.Add(codigo.ToUpperInvariant());
            }
        }

        return codigos.Count == 0
            ? throw new AccesoDenegadoException("No tienes ningún área asignada para ver este reporte.")
            : [.. codigos];
    }

    // ---------- Vencimientos ----------

    public async Task<ReporteVencimientosDto> VencimientosAsync(Actor actor, FiltroVencimientos filtro, int pagina = 1, int tamano = 25, CancellationToken ct = default)
    {
        var (numero, tam) = ReglasBusqueda.NormalizarPagina(pagina, tamano, 25);
        var ahora = reloj.GetUtcNow().UtcDateTime;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var baseQ = await ConsultaVencimientosAsync(db, actor, filtro, ct);

        var limite7 = ahora.AddDays(ReglasVencimiento.DiasAvisoUrgente);
        var limite30 = ahora.AddDays(ReglasVencimiento.DiasAvisoPrimero);
        var totales = new TotalesVencimientos(
            Vencidos: await baseQ.CountAsync(r => r.FechaProximaRevisionUtc <= ahora, ct),
            Urgentes: await baseQ.CountAsync(r => r.FechaProximaRevisionUtc > ahora && r.FechaProximaRevisionUtc <= limite7, ct),
            PorVencer: await baseQ.CountAsync(r => r.FechaProximaRevisionUtc > limite7 && r.FechaProximaRevisionUtc <= limite30, ct),
            AlDia: await baseQ.CountAsync(r => r.FechaProximaRevisionUtc > limite30, ct));

        var filtrada = AplicarEstado(baseQ, filtro.Estado, ahora);
        var total = await filtrada.CountAsync(ct);
        var filas = await ProyectarVencimientos(db, filtrada).Skip((numero - 1) * tam).Take(tam).ToListAsync(ct);

        return new ReporteVencimientosDto(
            new Pagina<FilaVencimientoDto>([.. filas.Select(f => AFila(f, ahora))], numero, tam, total),
            totales);
    }

    public async Task<IReadOnlyList<FilaVencimientoDto>> VencimientosCompletoAsync(Actor actor, FiltroVencimientos filtro, CancellationToken ct = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var baseQ = await ConsultaVencimientosAsync(db, actor, filtro, ct);
        var filas = await ProyectarVencimientos(db, AplicarEstado(baseQ, filtro.Estado, ahora))
            .Take(ReglasBusqueda.MaximoFilasExportacion)
            .ToListAsync(ct);

        return [.. filas.Select(f => AFila(f, ahora))];
    }

    /// <summary>Vigentes de documentos activos con fecha de próxima revisión, dentro del alcance del actor y con los filtros menos el de estado.</summary>
    private static async Task<IQueryable<RevisionDocumento>> ConsultaVencimientosAsync(
        AppDbContext db, Actor actor, FiltroVencimientos filtro, CancellationToken ct)
    {
        var areas = await AreasVisiblesAsync(db, actor, ct);
        var texto = ReglasReporte.LimpiarTexto(filtro.Texto, "Código o título");

        var consulta = db.Revisiones.AsNoTracking()
            .Where(r => r.Estado == EstadoRevision.Vigente
                        && r.Documento!.Estado == EstadoDocumento.Activo
                        && r.FechaProximaRevisionUtc != null);

        if (areas is not null)
        {
            consulta = consulta.Where(r => areas.Contains(r.Documento!.Area!.Codigo));
        }

        if (filtro.AreaId is { } area)
        {
            consulta = consulta.Where(r => r.Documento!.AreaId == area);
        }

        if (filtro.TipoDocumentoId is { } tipo)
        {
            consulta = consulta.Where(r => r.Documento!.TipoDocumentoId == tipo);
        }

        if (texto is not null)
        {
            foreach (var termino in ReglasBusqueda.Terminos(texto))
            {
                var patron = $"%{ReglasBusqueda.EscaparLike(termino)}%";
                consulta = consulta.Where(r =>
                    EF.Functions.Like(EF.Functions.Collate(r.Documento!.Codigo, Intercalacion), patron, CaracterDeEscape)
                    || EF.Functions.Like(EF.Functions.Collate(r.Documento.Titulo, Intercalacion), patron, CaracterDeEscape));
            }
        }

        return consulta;
    }

    private static IQueryable<RevisionDocumento> AplicarEstado(IQueryable<RevisionDocumento> consulta, FiltroEstadoVencimiento estado, DateTime ahora)
    {
        var limite30 = ahora.AddDays(ReglasVencimiento.DiasAvisoPrimero);
        return estado switch
        {
            FiltroEstadoVencimiento.SoloVencidos => consulta.Where(r => r.FechaProximaRevisionUtc <= ahora),
            FiltroEstadoVencimiento.PorVencer => consulta.Where(r => r.FechaProximaRevisionUtc > ahora && r.FechaProximaRevisionUtc <= limite30),
            FiltroEstadoVencimiento.Todos => consulta,
            _ => consulta.Where(r => r.FechaProximaRevisionUtc <= limite30),
        };
    }

    private sealed record FilaVencimientoCruda(
        int DocumentoId, int RevisionId, string Codigo, string Titulo, string Area, string Tipo, int Mayor, int Menor,
        DateTime Proxima, string Responsable, string? Dueno, bool Pendiente);

    private static IQueryable<FilaVencimientoCruda> ProyectarVencimientos(AppDbContext db, IQueryable<RevisionDocumento> consulta) =>
        consulta.OrderBy(r => r.FechaProximaRevisionUtc).ThenBy(r => r.Documento!.Codigo)
            .Select(r => new FilaVencimientoCruda(
                r.DocumentoId, r.Id, r.Documento!.Codigo, r.Documento.Titulo,
                r.Documento.Area!.Codigo + " – " + r.Documento.Area.Nombre,
                r.Documento.TipoDocumento!.Nombre, r.Mayor, r.Menor,
                r.FechaProximaRevisionUtc!.Value,
                r.Documento.Responsable!.NombreVisible,
                r.Documento.Area.Dueno != null ? r.Documento.Area.Dueno.NombreVisible : null,
                db.SolicitudesSinCambios.Any(s => s.RevisionId == r.Id && s.Estado == EstadoSolicitudSinCambios.Pendiente)));

    private static FilaVencimientoDto AFila(FilaVencimientoCruda f, DateTime ahora) => new(
        f.DocumentoId, f.RevisionId, f.Codigo, f.Titulo, f.Area, f.Tipo, RevisionDocumento.FormatearEtiqueta(f.Mayor, f.Menor),
        f.Proxima, ReglasVencimiento.Clasificar(f.Proxima, ahora), ReglasReporte.DiasParaVencer(f.Proxima, ahora),
        f.Responsable, f.Dueno, f.Pendiente);

    // ---------- Tareas pendientes ----------

    public async Task<ReporteTareasDto> TareasAsync(Actor actor, FiltroTareas filtro, CancellationToken ct = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var areas = await AreasVisiblesAsync(db, actor, ct);

        // Etapas: solo la vigente de cada flujo en curso (las siguientes todavía no le tocan a nadie).
        var pasos = db.PasosInstancia.AsNoTracking()
            .Where(p => p.Estado == EstadoPaso.Pendiente
                        && p.Instancia!.Estado == EstadoFlujo.EnCurso
                        && p.Orden == p.Instancia.Pasos.Where(x => x.Estado == EstadoPaso.Pendiente).Min(x => x.Orden));

        var solicitudes = db.SolicitudesSinCambios.AsNoTracking().Where(s => s.Estado == EstadoSolicitudSinCambios.Pendiente);

        if (areas is not null)
        {
            pasos = pasos.Where(p => areas.Contains(p.Instancia!.Revision!.Documento!.Area!.Codigo));
            solicitudes = solicitudes.Where(s => areas.Contains(s.Revision!.Documento!.Area!.Codigo));
        }

        if (filtro.AreaId is { } area)
        {
            pasos = pasos.Where(p => p.Instancia!.Revision!.Documento!.AreaId == area);
            solicitudes = solicitudes.Where(s => s.Revision!.Documento!.AreaId == area);
        }

        var filasPasos = await pasos.Select(p => new
        {
            p.AsignadoAId,
            Usuario = p.AsignadoA!.NombreVisible,
            p.Clase,
            p.Nombre,
            DocumentoId = p.Instancia!.Revision!.DocumentoId,
            p.Instancia.Revision.Documento!.Codigo,
            p.Instancia.Revision.Documento.Titulo,
            Area = p.Instancia.Revision.Documento.Area!.Codigo + " – " + p.Instancia.Revision.Documento.Area.Nombre,
            p.Instancia.Revision.Mayor,
            p.Instancia.Revision.Menor,
            p.Instancia.FechaInicioUtc,
            // Desde cuándo le toca: cuando se resolvió la etapa anterior (o el envío, si es la primera).
            UltimaDecision = p.Instancia.Pasos.Max(x => x.FechaDecisionUtc),
        }).ToListAsync(ct);

        var filasSolicitudes = await solicitudes.Select(s => new
        {
            s.SolicitanteId,
            DocumentoId = s.Revision!.DocumentoId,
            s.Revision.Documento!.Codigo,
            s.Revision.Documento.Titulo,
            Area = s.Revision.Documento.Area!.Codigo + " – " + s.Revision.Documento.Area.Nombre,
            s.Revision.Mayor,
            s.Revision.Menor,
            s.FechaSolicitudUtc,
            DuenoId = s.Revision.Documento.Area.DuenoId,
            Dueno = s.Revision.Documento.Area.Dueno != null ? s.Revision.Documento.Area.Dueno.NombreVisible : null,
            DuenoActivo = s.Revision.Documento.Area.Dueno != null && s.Revision.Documento.Area.Dueno.Activo,
            Solicitante = s.Solicitante!.NombreVisible,
        }).ToListAsync(ct);

        var filas = new List<FilaTareaDto>();

        foreach (var p in filasPasos)
        {
            var desde = p.UltimaDecision ?? p.FechaInicioUtc;
            filas.Add(new FilaTareaDto(
                p.AsignadoAId, p.Usuario, p.Clase == TipoPaso.Revision ? TipoTareaReporte.Revision : TipoTareaReporte.Aprobacion,
                p.DocumentoId, p.Codigo, p.Titulo, p.Area, RevisionDocumento.FormatearEtiqueta(p.Mayor, p.Menor), p.Nombre,
                desde, ReglasReporte.DiasEsperando(desde, ahora)));
        }

        foreach (var s in filasSolicitudes)
        {
            // Mismo criterio que el aviso: la decide el dueño del área; si no hay, está inactivo o es quien propuso, los Administradores.
            var dueno = s.DuenoId is { } id && s.DuenoActivo && id != s.SolicitanteId;
            filas.Add(new FilaTareaDto(
                dueno ? s.DuenoId!.Value : Guid.Empty,
                dueno ? s.Dueno! : "Administradores (el área no tiene dueño que pueda decidir)",
                TipoTareaReporte.DecisionSinCambios, s.DocumentoId, s.Codigo, s.Titulo, s.Area,
                RevisionDocumento.FormatearEtiqueta(s.Mayor, s.Menor), $"Propuesto por {s.Solicitante}",
                s.FechaSolicitudUtc, ReglasReporte.DiasEsperando(s.FechaSolicitudUtc, ahora)));
        }

        if (filtro.Tipo is { } tipo)
        {
            filas = [.. filas.Where(f => f.Tipo == tipo)];
        }

        // El resumen por persona no se ve afectado por el filtro de persona: así el selector conserva todas las opciones.
        var resumen = filas
            .GroupBy(f => (f.UsuarioId, f.Usuario))
            .Select(g => new ResumenTareasUsuarioDto(g.Key.UsuarioId, g.Key.Usuario, g.Count(), g.Max(f => f.DiasEsperando)))
            .OrderByDescending(r => r.Total).ThenBy(r => r.Usuario)
            .ToList();

        if (filtro.UsuarioId is { } usuarioId)
        {
            filas = [.. filas.Where(f => f.UsuarioId == usuarioId)];
        }

        return new ReporteTareasDto(
            [.. filas.OrderBy(f => f.Usuario).ThenByDescending(f => f.DiasEsperando).ThenBy(f => f.Codigo).Take(ReglasBusqueda.MaximoFilasExportacion)],
            resumen);
    }

    // ---------- Historial de un documento ----------

    public async Task<HistorialDocumentoDto> HistorialDocumentoAsync(Actor actor, int documentoId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var documento = await db.Documentos.AsNoTracking()
            .Include(d => d.Area).Include(d => d.TipoDocumento).Include(d => d.Responsable)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (!await PermisosDocumento.PuedeVerDocumentoAsync(db, actor, documento.Area!.Codigo, documentoId, ct))
        {
            throw new AccesoDenegadoException("No tienes permiso para ver el historial de este documento.");
        }

        var revisiones = await db.Revisiones.AsNoTracking().Include(r => r.Autor)
            .Where(r => r.DocumentoId == documentoId)
            .OrderBy(r => r.Mayor).ThenBy(r => r.Menor).ThenBy(r => r.Id)
            .ToListAsync(ct);

        var envios = await db.InstanciasFlujo.AsNoTracking()
            .Include(i => i.IniciadoPor)
            .Include(i => i.Pasos).ThenInclude(p => p.AsignadoA)
            .Include(i => i.Pasos).ThenInclude(p => p.DecididoPor)
            .Where(i => i.Revision!.DocumentoId == documentoId)
            .OrderBy(i => i.Numero)
            .ToListAsync(ct);

        var solicitudes = await db.SolicitudesSinCambios.AsNoTracking()
            .Include(s => s.Solicitante).Include(s => s.DecididaPor)
            .Where(s => s.Revision!.DocumentoId == documentoId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);

        var porRevision = revisiones.Select(r => new HistorialRevisionDto(
            r.Id, r.Etiqueta, r.Estado, r.MotivoCambio, r.DescripcionCambio, r.Autor!.NombreVisible,
            r.FechaCreacionUtc, r.FechaAprobacionUtc, r.FechaVigenciaUtc, r.FechaProximaRevisionUtc,
            [.. envios.Where(i => i.RevisionId == r.Id).Select(i => new HistorialEnvioDto(
                i.Numero, i.Estado, i.FechaInicioUtc, i.FechaFinUtc, i.IniciadoPor!.NombreVisible, i.MotivoCierre,
                [.. i.Pasos.OrderBy(p => p.Orden).Select(p => new HistorialPasoDto(
                    p.Orden, p.Clase, p.Nombre, p.AsignadoA!.NombreVisible, p.Estado,
                    p.DecididoPor?.NombreVisible, p.FechaDecisionUtc, p.Comentario))]))],
            [.. solicitudes.Where(s => s.RevisionId == r.Id).Select(s => new SolicitudSinCambiosDto(
                s.Id, s.RevisionId, documentoId, documento.Codigo, documento.Titulo, r.Etiqueta, s.Solicitante!.NombreVisible,
                s.Comentario, s.Estado, s.FechaSolicitudUtc, s.FechaProximaAnteriorUtc, s.FechaProximaNuevaUtc,
                s.DecididaPor?.NombreVisible, s.ComentarioDecision, s.FechaDecisionUtc))]))
            .ToList();

        return new HistorialDocumentoDto(
            documento.Id, documento.Codigo, documento.Titulo, documento.Area.Codigo + " – " + documento.Area.Nombre,
            documento.TipoDocumento!.Nombre, documento.Estado, documento.Responsable!.NombreVisible,
            documento.FechaCreacionUtc, porRevision);
    }
}
