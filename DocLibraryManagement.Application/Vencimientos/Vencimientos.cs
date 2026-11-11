using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Vencimientos;

/// <summary>Dónde está una revisión vigente respecto de su fecha de próxima revisión (7.4).</summary>
public enum EstadoVencimiento
{
    /// <summary>Falta más de 30 días, o no tiene fecha.</summary>
    AlDia = 0,

    /// <summary>Faltan 30 días o menos (y más de 7).</summary>
    PorVencer30 = 1,

    /// <summary>Faltan 7 días o menos.</summary>
    PorVencer7 = 2,

    /// <summary>La fecha ya llegó. El documento SIGUE vigente: solo se marca y se reporta.</summary>
    Vencido = 3,
}

/// <summary>Reglas puras de la revisión periódica. Sin acceso a datos: se prueban con fechas.</summary>
public static class ReglasVencimiento
{
    public const int DiasAvisoPrimero = 30;
    public const int DiasAvisoUrgente = 7;

    /// <summary>Con cuántos días de anticipación se avisa que una revisión aprobada está por entrar en vigor.</summary>
    public const int DiasAvisoVigenciaProxima = 3;

    public const int MaximoComentario = 1000;

    public static EstadoVencimiento Clasificar(DateTime? proximaRevisionUtc, DateTime ahoraUtc)
    {
        if (proximaRevisionUtc is not { } proxima)
        {
            return EstadoVencimiento.AlDia;
        }

        var restante = proxima - ahoraUtc;
        if (restante <= TimeSpan.Zero)
        {
            return EstadoVencimiento.Vencido;
        }

        if (restante <= TimeSpan.FromDays(DiasAvisoUrgente))
        {
            return EstadoVencimiento.PorVencer7;
        }

        return restante <= TimeSpan.FromDays(DiasAvisoPrimero) ? EstadoVencimiento.PorVencer30 : EstadoVencimiento.AlDia;
    }

    /// <summary>Días que faltan, redondeados hacia arriba (faltan 6 h → «1 día»). Negativo o cero si ya venció.</summary>
    public static int DiasRestantes(DateTime proximaRevisionUtc, DateTime ahoraUtc)
    {
        var restante = proximaRevisionUtc - ahoraUtc;
        return restante <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(restante.TotalDays);
    }

    /// <summary>
    /// «Revisado sin cambios» solo tiene sentido cuando ya toca revisar (dentro de la ventana de aviso o vencido).
    /// Proponerlo con meses de anticipación permitiría empujar el vencimiento sin haber revisado nada.
    /// </summary>
    public static bool PuedeProponerSinCambios(DateTime? proximaRevisionUtc, DateTime ahoraUtc) =>
        Clasificar(proximaRevisionUtc, ahoraUtc) != EstadoVencimiento.AlDia;

    /// <summary>
    /// Nueva fecha tras aprobar «sin cambios»: un periodo completo contado desde la fecha vigente si todavía no
    /// vence (no se pierden los días que faltaban) o desde hoy si ya venció (no se hereda el atraso).
    /// </summary>
    public static DateTime NuevaFechaTrasRevisionSinCambios(DateTime proximaActualUtc, DateTime ahoraUtc, int mesesRevision) =>
        (proximaActualUtc > ahoraUtc ? proximaActualUtc : ahoraUtc).AddMonths(mesesRevision);

    /// <summary>Marca estable de una fecha para las claves de idempotencia (distingue cada ciclo de revisión).</summary>
    public static string MarcaFecha(DateTime utc) => utc.ToString("yyyyMMddHHmm");
}

public sealed record SolicitudSinCambiosDto(
    int Id,
    int RevisionId,
    int DocumentoId,
    string CodigoDocumento,
    string Titulo,
    string Etiqueta,
    string Solicitante,
    string Comentario,
    EstadoSolicitudSinCambios Estado,
    DateTime FechaSolicitudUtc,
    DateTime FechaProximaAnteriorUtc,
    DateTime? FechaProximaNuevaUtc,
    string? DecididaPor,
    string? ComentarioDecision,
    DateTime? FechaDecisionUtc);

/// <summary>
/// Lo que la pantalla de un documento muestra de su revisión periódica y lo que el usuario actual puede hacer. Los
/// permisos ya vienen resueltos: la pantalla no repite las reglas, solo las refleja.
/// </summary>
public sealed record PanelRevisionPeriodicaDto(
    int RevisionId,
    string Etiqueta,
    DateTime? FechaProximaRevisionUtc,
    EstadoVencimiento Vencimiento,
    int DiasRestantes,
    string? NombreDueno,
    SolicitudSinCambiosDto? Pendiente,
    bool PuedeProponer,
    bool PuedeDecidir,
    bool PuedeCancelar,
    IReadOnlyList<SolicitudSinCambiosDto> Historial);

/// <summary>Resumen de una pasada de vencimientos (para el registro del proceso de fondo).</summary>
public sealed record ResumenVencimientos(int AvisosEncolados, int RevisionesRevisadas);

public interface IVencimientosService
{
    /// <summary>
    /// Proceso del sistema: encola los avisos a 30 y 7 días, el «Vencido» (con el escalamiento al dueño del área o, si no
    /// hay, a los Administradores) y el aviso de vigencia próxima. Es idempotente: cada aviso lleva una clave y no se
    /// repite aunque se ejecute muchas veces. Un fallo en un documento no impide los demás.
    /// </summary>
    Task<ResumenVencimientos> ProcesarAsync(CancellationToken ct = default);

    /// <summary>
    /// El responsable (o un Administrador) declara que revisó la revisión VIGENTE y no requiere cambios. Solo cuando ya
    /// toca revisar (30 días o menos, o vencida). Avisa al dueño del área. Devuelve el id de la solicitud.
    /// </summary>
    Task<int> ProponerSinCambiosAsync(Actor actor, int revisionId, string comentario, CancellationToken ct = default);

    /// <summary>
    /// El dueño del área (o un Administrador) la aprueba o la rechaza, en una sola etapa. Nunca quien la propuso.
    /// Rechazar exige comentario. Aprobar extiende la fecha de próxima revisión.
    /// </summary>
    Task DecidirSinCambiosAsync(Actor actor, int solicitudId, bool aprobar, string? comentario, CancellationToken ct = default);

    /// <summary>Quien la propuso (o un Administrador) la retira mientras siga pendiente.</summary>
    Task CancelarSinCambiosAsync(Actor actor, int solicitudId, CancellationToken ct = default);

    /// <summary>
    /// Estado de la revisión periódica de una revisión VIGENTE y qué puede hacer el actor. Devuelve <c>null</c> si la
    /// revisión no es la vigente o el actor no participa en el documento (la pantalla simplemente no muestra el panel).
    /// </summary>
    Task<PanelRevisionPeriodicaDto?> PanelAsync(Actor actor, int revisionId, CancellationToken ct = default);

    /// <summary>Historial de solicitudes de una revisión (la más reciente primero). Lo ven quienes pueden ver el documento.</summary>
    Task<IReadOnlyList<SolicitudSinCambiosDto>> HistorialAsync(Actor actor, int revisionId, CancellationToken ct = default);

    /// <summary>Las pendientes que el actor puede decidir: las de las áreas de las que es dueño (todas, si es Administrador).</summary>
    Task<IReadOnlyList<SolicitudSinCambiosDto>> PendientesDeDecisionAsync(Actor actor, CancellationToken ct = default);
}
