using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Documentos;

public sealed record RevisionDetalleDto(
    int Id,
    int DocumentoId,
    string CodigoDocumento,
    string CodigoArea,
    string Titulo,
    string Etiqueta,
    EstadoRevision Estado,
    string MotivoCambio,
    string? DescripcionCambio,
    bool TienePdf,
    string? HashPdf,
    bool TieneFuente,
    string? HashFuente,
    Guid AutorId,
    string Autor,
    DateTime FechaCreacionUtc,
    DateTime? FechaAprobacionUtc,
    string? ComentarioUltimoRechazo,
    bool DevueltaPorRevocacion,
    bool EsPrimeraEmision,
    int TipoDocumentoId,
    int Mayor,
    int Menor,
    DateTime? FechaVigenciaUtc,
    DateTime? FechaProximaRevisionUtc)
{
    /// <summary>Una revisión menor es una corrección editorial con aprobación abreviada.</summary>
    public bool EsMenor => Menor > 0;
}

/// <param name="TieneTextoBuscable">El PDF tiene texto, así que se podrá encontrar por su contenido.</param>
/// <param name="Aviso">Qué decirle al autor cuando no se podrá buscar por contenido; null si todo está bien o es una fuente.</param>
public sealed record ResultadoSubida(bool TieneTextoBuscable, string? Aviso);

public interface IRevisionService
{
    /// <summary>
    /// Ver una revisión que no está vigente exige ser Administrador, Auditor, Autor del área
    /// o tener una etapa asignada en ella.
    /// </summary>
    Task<RevisionDetalleDto> ObtenerAsync(Actor actor, int revisionId, CancellationToken ct = default);

    /// <summary>Solo en Borrador y solo quien edita en el área del documento.</summary>
    Task ActualizarBorradorAsync(Actor actor, int revisionId, string motivoCambio, string? descripcionCambio, CancellationToken ct = default);

    /// <summary>
    /// Sube (o reemplaza) el PDF o la fuente de un borrador. Al subir un PDF se lee su texto para la búsqueda;
    /// si no tiene (escaneado) o no se pudo leer, la subida sí se acepta pero el resultado trae un aviso.
    /// </summary>
    Task<ResultadoSubida> SubirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, string nombreOriginal, Stream contenido, CancellationToken ct = default);

    /// <summary>Abre un archivo para descargarlo, verificando permisos y la integridad (hash).</summary>
    Task<ArchivoParaDescarga> AbrirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, CancellationToken ct = default);

    /// <summary>
    /// Abre un borrador nuevo a partir de la revisión vigente. Requiere que exista una vigente y que no haya
    /// otra revisión abierta. Una menor exige declarar que el cambio no altera requisitos, pasos ni
    /// responsabilidades, y que el área tenga dueño.
    /// </summary>
    Task<RevisionDetalleDto> CrearRevisionAsync(
        Actor actor,
        int documentoId,
        TipoRevisionNueva tipo,
        string motivoCambio,
        string? descripcionCambio,
        bool declaraQueNoAlteraRequisitos,
        CancellationToken ct = default);

    /// <summary>
    /// Descarta un borrador que NO es la primera emisión (para esa, ver <see cref="CancelarDocumentoNuevoAsync"/>).
    /// La revisión queda Cancelada y su número vuelve a quedar disponible.
    /// </summary>
    Task CancelarBorradorAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default);

    /// <summary>
    /// Cambia la fecha de entrada en vigor de una revisión APROBADA que todavía no entra en vigor. Solo el
    /// Administrador, con motivo. La fecha debe ser futura y de hasta 365 días; la aprobación se conserva.
    /// </summary>
    Task ReprogramarVigenciaAsync(Actor actor, int revisionId, DateTime nuevaVigenciaUtc, string motivo, CancellationToken ct = default);

    /// <summary>
    /// Anula la aprobación de una revisión APROBADA que todavía no entra en vigor y la devuelve a Borrador para
    /// corregirla. Solo el Administrador, con motivo. El autor debe reenviarla (flujo completo, envío nuevo); la
    /// vigente actual no se afecta. El flujo anterior queda en el historial como <c>Revocado</c>.
    /// </summary>
    Task RevocarAprobacionAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default);

    /// <summary>
    /// Baja de un documento sin sucesor. Solo el Administrador. La revisión vigente pasa a Obsoleta y
    /// cualquier borrador, flujo en curso o revisión aprobada pendiente se cancela.
    /// </summary>
    Task RetirarDocumentoAsync(Actor actor, int documentoId, string motivo, CancellationToken ct = default);

    /// <summary>
    /// Descarta un documento recién creado cuya primera revisión sigue en Borrador: la revisión queda
    /// Cancelada y el documento Retirado. El código no se reutiliza.
    /// </summary>
    Task CancelarDocumentoNuevoAsync(Actor actor, int documentoId, string motivo, CancellationToken ct = default);
}
