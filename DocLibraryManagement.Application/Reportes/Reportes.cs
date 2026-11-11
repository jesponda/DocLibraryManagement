using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Reportes;

// =====================================================================================================================
// Reglas puras
// =====================================================================================================================

public static class ReglasReporte
{
    public const int MaximoTextoFiltro = 200;

    /// <summary>
    /// Días que faltan para la fecha, con signo: positivos si aún no llega (redondeados hacia arriba), 0 si venció hoy y
    /// negativos si venció antes («-3» = venció hace 3 días).
    /// </summary>
    public static int DiasParaVencer(DateTime proximaUtc, DateTime ahoraUtc)
    {
        var diferencia = proximaUtc - ahoraUtc;
        return diferencia > TimeSpan.Zero
            ? (int)Math.Ceiling(diferencia.TotalDays)
            : -(int)Math.Floor((-diferencia).TotalDays);
    }

    /// <summary>Días completos que lleva esperando algo desde <paramref name="desdeUtc"/> (nunca negativo).</summary>
    public static int DiasEsperando(DateTime desdeUtc, DateTime ahoraUtc) =>
        Math.Max(0, (int)Math.Floor((ahoraUtc - desdeUtc).TotalDays));

    /// <summary>
    /// Convierte «desde» y «hasta» (días en la hora local de la planta, ambos incluidos) al rango UTC con que se consulta:
    /// desde el inicio del primer día hasta el inicio del día siguiente al último (excluido).
    /// </summary>
    public static (DateTime? DesdeUtc, DateTime? HastaExclusivoUtc) RangoUtc(DateOnly? desde, DateOnly? hasta, TimeZoneInfo zona)
    {
        if (desde is { } d && hasta is { } h && h < d)
        {
            throw new ReglaNegocioException("La fecha final no puede ser anterior a la inicial.");
        }

        return (desde is { } a ? InicioDeDiaUtc(a, zona) : null,
                hasta is { } b ? InicioDeDiaUtc(b.AddDays(1), zona) : null);
    }

    private static DateTime InicioDeDiaUtc(DateOnly dia, TimeZoneInfo zona)
    {
        var local = dia.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // En un cambio de horario la medianoche puede no existir: se toma la primera hora que sí existe.
        while (zona.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zona);
    }

    /// <summary>Texto de filtro sin espacios sobrantes; nulo si queda vacío. Rechaza lo desmesurado.</summary>
    public static string? LimpiarTexto(string? texto, string nombreCampo)
    {
        var limpio = (texto ?? "").Trim();
        if (limpio.Length > MaximoTextoFiltro)
        {
            throw new ReglaNegocioException($"El filtro «{nombreCampo}» no puede pasar de {MaximoTextoFiltro} caracteres.");
        }

        return limpio.Length == 0 ? null : limpio;
    }

    public static string NombreVencimiento(EstadoVencimiento estado) => estado switch
    {
        EstadoVencimiento.Vencido => "Vencida",
        EstadoVencimiento.PorVencer7 => "Vence en 7 días o menos",
        EstadoVencimiento.PorVencer30 => "Vence en 30 días o menos",
        _ => "Al día",
    };

    public static string NombreTipoTarea(TipoTareaReporte tipo) => tipo switch
    {
        TipoTareaReporte.Revision => "Revisión",
        TipoTareaReporte.Aprobacion => "Aprobación",
        _ => "Decidir «revisado sin cambios»",
    };

    public static string NombreEstadoPaso(EstadoPaso estado) => estado switch
    {
        EstadoPaso.Pendiente => "Pendiente",
        EstadoPaso.Aprobado => "Aprobada",
        EstadoPaso.Rechazado => "Rechazada",
        _ => "Omitida",
    };

    public static string NombreEstadoFlujo(EstadoFlujo estado) => estado switch
    {
        EstadoFlujo.EnCurso => "En curso",
        EstadoFlujo.Aprobado => "Aprobado",
        EstadoFlujo.Rechazado => "Rechazado",
        EstadoFlujo.Cancelado => "Cancelado",
        _ => "Aprobación anulada",
    };

    public static string NombreEstadoRevision(EstadoRevision estado) => estado switch
    {
        EstadoRevision.Borrador => "Borrador",
        EstadoRevision.EnRevision => "En revisión",
        EstadoRevision.EnAprobacion => "En aprobación",
        EstadoRevision.Aprobado => "Aprobado",
        EstadoRevision.Vigente => "Vigente",
        EstadoRevision.Obsoleto => "Obsoleto",
        _ => "Cancelado",
    };

    public static string NombreSolicitud(EstadoSolicitudSinCambios estado) => estado switch
    {
        EstadoSolicitudSinCambios.Pendiente => "Pendiente",
        EstadoSolicitudSinCambios.Aprobada => "Aprobada",
        EstadoSolicitudSinCambios.Rechazada => "Rechazada",
        _ => "Cancelada",
    };
}

// =====================================================================================================================
// Vencimientos
// =====================================================================================================================

public enum FiltroEstadoVencimiento
{
    /// <summary>Lo que requiere atención: vencidos y los que vencen en 30 días o menos.</summary>
    ConAviso = 0,

    SoloVencidos = 1,

    /// <summary>Vencen en 30 días o menos y todavía no han vencido.</summary>
    PorVencer = 2,

    /// <summary>Todos los vigentes, también los que están al día.</summary>
    Todos = 3,
}

public sealed record FiltroVencimientos(
    FiltroEstadoVencimiento Estado = FiltroEstadoVencimiento.ConAviso,
    int? AreaId = null,
    int? TipoDocumentoId = null,
    string? Texto = null);

public sealed record FilaVencimientoDto(
    int DocumentoId,
    int RevisionId,
    string Codigo,
    string Titulo,
    string Area,
    string Tipo,
    string Etiqueta,
    DateTime FechaProximaRevisionUtc,
    EstadoVencimiento Vencimiento,
    int Dias,
    string Responsable,
    string? Dueno,
    bool SolicitudPendiente);

/// <summary>Cuántos documentos hay en cada situación (con los filtros de área, tipo y texto, sin el de estado).</summary>
public sealed record TotalesVencimientos(int Vencidos, int Urgentes, int PorVencer, int AlDia)
{
    public int Total => Vencidos + Urgentes + PorVencer + AlDia;
}

public sealed record ReporteVencimientosDto(Pagina<FilaVencimientoDto> Filas, TotalesVencimientos Totales);

// =====================================================================================================================
// Tareas pendientes
// =====================================================================================================================

public enum TipoTareaReporte
{
    Revision = 1,
    Aprobacion = 2,
    DecisionSinCambios = 3,
}

public sealed record FiltroTareas(Guid? UsuarioId = null, TipoTareaReporte? Tipo = null, int? AreaId = null);

/// <summary>Una tarea que alguien tiene pendiente hoy. <see cref="UsuarioId"/> vacío = «los Administradores» (el área no tiene dueño).</summary>
public sealed record FilaTareaDto(
    Guid UsuarioId,
    string Usuario,
    TipoTareaReporte Tipo,
    int DocumentoId,
    string Codigo,
    string Titulo,
    string Area,
    string Etiqueta,
    string Detalle,
    DateTime DesdeUtc,
    int DiasEsperando);

public sealed record ResumenTareasUsuarioDto(Guid UsuarioId, string Usuario, int Total, int DiasMaximo);

public sealed record ReporteTareasDto(IReadOnlyList<FilaTareaDto> Filas, IReadOnlyList<ResumenTareasUsuarioDto> PorUsuario);

// =====================================================================================================================
// Historial de un documento
// =====================================================================================================================

public sealed record HistorialPasoDto(
    int Orden,
    TipoPaso Clase,
    string Nombre,
    string AsignadoA,
    EstadoPaso Estado,
    string? DecididoPor,
    DateTime? FechaDecisionUtc,
    string? Comentario);

public sealed record HistorialEnvioDto(
    int Numero,
    EstadoFlujo Estado,
    DateTime FechaInicioUtc,
    DateTime? FechaFinUtc,
    string IniciadoPor,
    string? MotivoCierre,
    IReadOnlyList<HistorialPasoDto> Pasos);

public sealed record HistorialRevisionDto(
    int RevisionId,
    string Etiqueta,
    EstadoRevision Estado,
    string MotivoCambio,
    string? DescripcionCambio,
    string Autor,
    DateTime FechaCreacionUtc,
    DateTime? FechaAprobacionUtc,
    DateTime? FechaVigenciaUtc,
    DateTime? FechaProximaRevisionUtc,
    IReadOnlyList<HistorialEnvioDto> Envios,
    IReadOnlyList<SolicitudSinCambiosDto> SinCambios);

public sealed record HistorialDocumentoDto(
    int DocumentoId,
    string Codigo,
    string Titulo,
    string Area,
    string Tipo,
    EstadoDocumento Estado,
    string Responsable,
    DateTime FechaCreacionUtc,
    IReadOnlyList<HistorialRevisionDto> Revisiones);

public interface IReportesService
{
    /// <summary>
    /// Documentos por vencer y vencidos. Alcance: Administrador y Auditor ven todo; un Autor, las áreas donde edita; un
    /// Aprobador, las áreas de las que es dueño. Sin ninguna área, no hay reporte (<see cref="AccesoDenegadoException"/>).
    /// </summary>
    Task<ReporteVencimientosDto> VencimientosAsync(Actor actor, FiltroVencimientos filtro, int pagina = 1, int tamano = 25, CancellationToken ct = default);

    /// <summary>Todas las filas del filtro para exportar, con tope (<see cref="ReglasBusqueda.MaximoFilasExportacion"/>).</summary>
    Task<IReadOnlyList<FilaVencimientoDto>> VencimientosCompletoAsync(Actor actor, FiltroVencimientos filtro, CancellationToken ct = default);

    /// <summary>
    /// Quién tiene qué pendiente hoy: la etapa vigente de cada flujo en curso y las solicitudes «revisado sin cambios»
    /// esperando al dueño del área. Mismo alcance que el reporte de vencimientos.
    /// </summary>
    Task<ReporteTareasDto> TareasAsync(Actor actor, FiltroTareas filtro, CancellationToken ct = default);

    /// <summary>
    /// Todo lo ocurrido con un documento: revisiones, envíos con la decisión de cada etapa y solicitudes «sin cambios».
    /// Lo ve quien puede ver el documento.
    /// </summary>
    Task<HistorialDocumentoDto> HistorialDocumentoAsync(Actor actor, int documentoId, CancellationToken ct = default);
}

// =====================================================================================================================
// Bitácora
// =====================================================================================================================

/// <param name="DesdeUtc">Incluido.</param>
/// <param name="HastaExclusivoUtc">Excluido (el inicio del día siguiente al último que se quiere ver).</param>
/// <param name="Accion">Prefijo: «Revision.» trae todas las acciones de revisiones.</param>
public sealed record FiltroAuditoria(
    DateTime? DesdeUtc = null,
    DateTime? HastaExclusivoUtc = null,
    string? Usuario = null,
    string? Accion = null,
    string? Entidad = null,
    string? EntidadId = null,
    string? Texto = null);

public sealed record EventoAuditoriaDto(
    long Id,
    DateTime FechaUtc,
    string Usuario,
    string Accion,
    string Entidad,
    string? EntidadId,
    string? ValorAnterior,
    string? ValorNuevo,
    string? Comentario,
    string? DireccionIp);

public interface IAuditoriaService
{
    /// <summary>Consulta de la bitácora, de lo más reciente a lo más antiguo. Solo Administrador y Auditor.</summary>
    Task<Pagina<EventoAuditoriaDto>> ConsultarAsync(Actor actor, FiltroAuditoria filtro, int pagina = 1, int tamano = 50, CancellationToken ct = default);

    /// <summary>Las filas del filtro para exportar, con tope (<see cref="ReglasBusqueda.MaximoFilasExportacion"/>).</summary>
    Task<IReadOnlyList<EventoAuditoriaDto>> ExportarAsync(Actor actor, FiltroAuditoria filtro, CancellationToken ct = default);

    /// <summary>Entidades que aparecen en la bitácora, para el filtro.</summary>
    Task<IReadOnlyList<string>> EntidadesAsync(Actor actor, CancellationToken ct = default);

    /// <summary>Acciones que aparecen en la bitácora, para sugerir en el filtro.</summary>
    Task<IReadOnlyList<string>> AccionesAsync(Actor actor, CancellationToken ct = default);
}

// =====================================================================================================================
// Exportación
// =====================================================================================================================

/// <summary>Una fecha que se muestra sin hora (por ejemplo, la próxima revisión).</summary>
public readonly record struct SoloFecha(DateTime? Utc);

/// <summary>
/// Datos listos para exportar. Cada celda es texto, número, <c>bool</c>, <c>DateTime</c> (UTC; se muestra en hora local con hora),
/// <see cref="SoloFecha"/> o nulo.
/// </summary>
public sealed record TablaExportable(string NombreHoja, IReadOnlyList<string> Encabezados, IReadOnlyList<IReadOnlyList<object?>> Filas);

public interface IExportadorTablas
{
    /// <summary>CSV UTF-8 con BOM (para que Excel respete los acentos), con protección contra inyección de fórmulas.</summary>
    byte[] ACsv(TablaExportable tabla);

    byte[] AXlsx(TablaExportable tabla);
}

/// <summary>Convierte los resultados de cada reporte en tablas exportables. Puro: no toca datos.</summary>
public static class TablasReporte
{
    public static TablaExportable Vencimientos(IReadOnlyList<FilaVencimientoDto> filas) => new(
        "Vencimientos",
        ["Código", "Título", "Área", "Tipo", "Revisión", "Próxima revisión", "Estado", "Días", "Responsable", "Dueño del área", "Solicitud pendiente"],
        [.. filas.Select(f => (IReadOnlyList<object?>)
        [
            f.Codigo, f.Titulo, f.Area, f.Tipo, f.Etiqueta, new SoloFecha(f.FechaProximaRevisionUtc),
            ReglasReporte.NombreVencimiento(f.Vencimiento), f.Dias, f.Responsable, f.Dueno, f.SolicitudPendiente,
        ])]);

    public static TablaExportable Tareas(IReadOnlyList<FilaTareaDto> filas) => new(
        "Tareas pendientes",
        ["Persona", "Tarea", "Código", "Título", "Área", "Revisión", "Etapa", "Desde", "Días esperando"],
        [.. filas.Select(f => (IReadOnlyList<object?>)
        [
            f.Usuario, ReglasReporte.NombreTipoTarea(f.Tipo), f.Codigo, f.Titulo, f.Area, f.Etiqueta, f.Detalle, f.DesdeUtc, f.DiasEsperando,
        ])]);

    public static TablaExportable Auditoria(IReadOnlyList<EventoAuditoriaDto> filas) => new(
        "Bitácora",
        ["Fecha", "Usuario", "Acción", "Entidad", "Identificador", "Valor anterior", "Valor nuevo", "Comentario", "IP"],
        [.. filas.Select(f => (IReadOnlyList<object?>)
        [
            f.FechaUtc, f.Usuario, f.Accion, f.Entidad, f.EntidadId, f.ValorAnterior, f.ValorNuevo, f.Comentario, f.DireccionIp,
        ])]);

    /// <summary>
    /// Una fila por etapa de cada envío; una revisión sin envíos (o una solicitud «sin cambios») ocupa su propia fila,
    /// de modo que nada del historial queda fuera de la exportación.
    /// </summary>
    public static TablaExportable Historial(HistorialDocumentoDto h)
    {
        var filas = new List<IReadOnlyList<object?>>();

        foreach (var r in h.Revisiones)
        {
            var base_ = new object?[] { r.Etiqueta, ReglasReporte.NombreEstadoRevision(r.Estado), r.MotivoCambio, r.Autor, r.FechaCreacionUtc, r.FechaAprobacionUtc, r.FechaVigenciaUtc };

            if (r.Envios.Count == 0 && r.SinCambios.Count == 0)
            {
                filas.Add([.. base_, null, null, null, null, null, null, null, null]);
            }

            foreach (var e in r.Envios)
            {
                if (e.Pasos.Count == 0)
                {
                    filas.Add([.. base_, $"Envío {e.Numero}: {ReglasReporte.NombreEstadoFlujo(e.Estado)}", null, null, null, null, null, null, e.MotivoCierre]);
                }

                foreach (var p in e.Pasos)
                {
                    filas.Add([.. base_,
                        $"Envío {e.Numero}: {ReglasReporte.NombreEstadoFlujo(e.Estado)}", $"{p.Orden}. {p.Nombre}",
                        p.AsignadoA, ReglasReporte.NombreEstadoPaso(p.Estado), p.DecididoPor, p.FechaDecisionUtc, null, p.Comentario]);
                }
            }

            foreach (var s in r.SinCambios)
            {
                filas.Add([.. base_,
                    "Revisado sin cambios", $"Propuso: {s.Solicitante}", null, ReglasReporte.NombreSolicitud(s.Estado),
                    s.DecididaPor, s.FechaDecisionUtc, s.FechaProximaNuevaUtc is null ? null : new SoloFecha(s.FechaProximaNuevaUtc),
                    string.IsNullOrEmpty(s.ComentarioDecision) ? s.Comentario : $"{s.Comentario} | Decisión: {s.ComentarioDecision}"]);
            }
        }

        return new TablaExportable(
            "Historial " + h.Codigo,
            ["Revisión", "Estado de la revisión", "Motivo del cambio", "Autor", "Creada", "Aprobada", "Vigente desde",
             "Envío", "Etapa", "Asignada a", "Resultado", "Decidió", "Fecha de decisión", "Nueva próxima revisión", "Comentario"],
            filas);
    }
}
