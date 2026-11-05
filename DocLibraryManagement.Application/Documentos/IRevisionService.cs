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
    bool EsPrimeraEmision,
    int TipoDocumentoId);

public interface IRevisionService
{
    /// <summary>
    /// Ver una revisión que no está vigente exige ser Administrador, Auditor, Autor del área
    /// o tener una etapa asignada en ella.
    /// </summary>
    Task<RevisionDetalleDto> ObtenerAsync(Actor actor, int revisionId, CancellationToken ct = default);

    /// <summary>Solo en Borrador y solo quien edita en el área del documento.</summary>
    Task ActualizarBorradorAsync(Actor actor, int revisionId, string motivoCambio, string? descripcionCambio, CancellationToken ct = default);

    /// <summary>Sube (o reemplaza) el PDF o la fuente de un borrador.</summary>
    Task SubirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, string nombreOriginal, Stream contenido, CancellationToken ct = default);

    /// <summary>Abre un archivo para descargarlo, verificando permisos y la integridad (hash).</summary>
    Task<ArchivoParaDescarga> AbrirArchivoAsync(Actor actor, int revisionId, TipoArchivo tipo, CancellationToken ct = default);

    /// <summary>
    /// Descarta un documento recién creado cuya primera revisión sigue en Borrador: la revisión queda
    /// Cancelada y el documento Retirado. El código no se reutiliza.
    /// </summary>
    Task CancelarDocumentoNuevoAsync(Actor actor, int documentoId, string motivo, CancellationToken ct = default);
}
