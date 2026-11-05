using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Documentos;

public sealed record NuevoDocumento(string Titulo, int AreaId, int? SubAreaId, int TipoDocumentoId);

public sealed record DocumentoResumenDto(
    int Id,
    string Codigo,
    string Titulo,
    string Area,
    string? SubArea,
    string Tipo,
    EstadoDocumento Estado,
    string UltimaRevision,
    EstadoRevision EstadoUltimaRevision);

public sealed record RevisionDto(
    int Id,
    string Etiqueta,
    EstadoRevision Estado,
    string MotivoCambio,
    string Autor,
    DateTime FechaCreacionUtc);

public sealed record DocumentoDetalleDto(
    int Id,
    string Codigo,
    string Titulo,
    int AreaId,
    string CodigoArea,
    string Area,
    int? SubAreaId,
    string? SubArea,
    string Tipo,
    EstadoDocumento Estado,
    string Responsable,
    DateTime FechaCreacionUtc,
    IReadOnlyList<RevisionDto> Revisiones);

public interface IDocumentoService
{
    /// <summary>
    /// Crea el documento con su código automático (TTT-AAA-NNN) y su primera revisión (Rev.00, Borrador).
    /// El actor debe poder editar en el área.
    /// </summary>
    Task<DocumentoDetalleDto> CrearAsync(Actor actor, NuevoDocumento nuevo, CancellationToken ct = default);

    /// <summary>
    /// Documentos que el actor puede gestionar: los de las áreas donde edita
    /// (el Administrador y el Auditor ven todos). La lista maestra para lectura llega en la Fase 3.
    /// </summary>
    Task<IReadOnlyList<DocumentoResumenDto>> ListarGestionablesAsync(Actor actor, CancellationToken ct = default);

    Task<DocumentoDetalleDto?> ObtenerAsync(Actor actor, int documentoId, CancellationToken ct = default);

    /// <summary>Cambia la subárea (clasificación). El área no se puede cambiar: forma parte del código.</summary>
    Task CambiarSubAreaAsync(Actor actor, int documentoId, int? subAreaId, CancellationToken ct = default);
}
