using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Consulta;

public sealed record Pagina<T>(IReadOnlyList<T> Elementos, int PaginaActual, int TamanoPagina, int Total)
{
    public int TotalPaginas => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)TamanoPagina);
}

public sealed record FiltroListaMaestra(string? Texto = null, int? AreaId = null, int? SubAreaId = null, int? TipoDocumentoId = null);

/// <summary>Una fila de la lista maestra: la revisión VIGENTE de un documento activo.</summary>
public sealed record FilaListaMaestra(
    int DocumentoId,
    int RevisionId,
    string Codigo,
    string Titulo,
    string Area,
    string? SubArea,
    string Tipo,
    string Revision,
    DateTime? FechaAprobacionUtc,
    DateTime? FechaVigenciaUtc,
    DateTime? FechaProximaRevisionUtc,
    string Responsable);

public sealed record ConsultaBusqueda(string Texto, int? AreaId = null, int? TipoDocumentoId = null, bool IncluirObsoletos = false);

/// <summary>Trozo de texto de un fragmento de resultado; los resaltados son las coincidencias.</summary>
public sealed record FragmentoTexto(string Texto, bool Resaltado);

public sealed record ResultadoBusqueda(
    int DocumentoId,
    int RevisionId,
    string Codigo,
    string Titulo,
    string Area,
    string Tipo,
    string Revision,
    EstadoRevision Estado,
    bool CoincideEnCodigoOTitulo,
    IReadOnlyList<FragmentoTexto> Fragmento);

public sealed record DocumentoVigenteDto(
    int DocumentoId,
    int RevisionId,
    string Codigo,
    string Titulo,
    string Area,
    string CodigoArea,
    string? SubArea,
    string Tipo,
    string Revision,
    DateTime? FechaAprobacionUtc,
    DateTime? FechaVigenciaUtc,
    DateTime? FechaProximaRevisionUtc,
    string Responsable);

/// <summary>
/// Consulta para lectura (7.3, 7.7). Lo que aquí se devuelve es lectura abierta a cualquier usuario
/// autenticado, por eso SOLO incluye revisiones vigentes de documentos activos, salvo la búsqueda de
/// Administrador y Auditor, que puede incluir obsoletas.
/// </summary>
public interface IConsultaService
{
    Task<Pagina<FilaListaMaestra>> ListaMaestraAsync(FiltroListaMaestra filtro, int pagina = 1, int tamano = 25, CancellationToken ct = default);

    /// <summary>La lista completa con el mismo filtro, para exportar. Tiene un tope (<see cref="ReglasBusqueda.MaximoFilasExportacion"/>).</summary>
    Task<IReadOnlyList<FilaListaMaestra>> ListaMaestraCompletaAsync(FiltroListaMaestra filtro, CancellationToken ct = default);

    /// <summary>
    /// Busca por código, título y contenido del PDF. Todas las palabras deben aparecer, sin distinguir
    /// mayúsculas ni acentos. Incluir obsoletas solo lo pueden pedir Administrador y Auditor.
    /// </summary>
    Task<Pagina<ResultadoBusqueda>> BuscarAsync(Actor actor, ConsultaBusqueda consulta, int pagina = 1, int tamano = 20, CancellationToken ct = default);

    /// <summary>Revisión vigente de un documento activo, o null si no hay (no existe, está retirado o no se ha publicado).</summary>
    Task<DocumentoVigenteDto?> ObtenerVigenteAsync(string codigo, CancellationToken ct = default);

    /// <summary>Identifica una revisión por código y etiqueta (p. ej. «Rev.01»). Los permisos los valida quien la abre.</summary>
    Task<(int DocumentoId, int RevisionId)?> ResolverRevisionAsync(string codigo, string etiqueta, CancellationToken ct = default);
}
