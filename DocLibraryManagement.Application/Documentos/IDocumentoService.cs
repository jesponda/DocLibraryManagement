using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Documentos;

/// <param name="SubAreaId">
/// La unidad del documento (forma parte del código). Puede omitirse solo si el área tiene una única subárea activa con
/// abreviatura: entonces se usa esa.
/// </param>
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

    /// <summary>
    /// Asigna la subárea a un documento anterior al estándar de código, que no tiene unidad. En los documentos nuevos la subárea
    /// (unidad) forma parte del código desde que se crean y <b>no se puede cambiar</b>, igual que el área.
    /// </summary>
    Task CambiarSubAreaAsync(Actor actor, int documentoId, int? subAreaId, CancellationToken ct = default);

    /// <summary>
    /// Personas que pueden quedar como responsable del documento: activas, Autores del área del documento (según su último
    /// acceso) o Administradores, sin contar al responsable actual. Solo Administrador.
    /// </summary>
    Task<IReadOnlyList<CandidatoResponsableDto>> CandidatosResponsableAsync(Actor actor, int documentoId, CancellationToken ct = default);

    /// <summary>
    /// Cambia el responsable de un documento activo (p. ej. porque la persona dejó la planta o cambió de puesto). Solo
    /// Administrador, con motivo. Avisa a la persona nueva y a la anterior, y queda en la bitácora. Los avisos de revisión
    /// periódica y el «revisado sin cambios» pasan a la persona nueva.
    /// </summary>
    Task CambiarResponsableAsync(Actor actor, int documentoId, Guid nuevoResponsableId, string motivo, CancellationToken ct = default);
}

public sealed record CandidatoResponsableDto(Guid Id, string NombreVisible, string Identificador);
