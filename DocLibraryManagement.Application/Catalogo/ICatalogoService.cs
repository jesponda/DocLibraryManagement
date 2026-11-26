using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Catalogo;

public sealed record TipoDocumentoDto(int Id, string Codigo, string Nombre, int MesesRevision, bool Activo);

public sealed record SubAreaDto(int Id, int AreaId, string Nombre, bool Activa, IReadOnlyList<int> TipoIds, string? Codigo = null);

public sealed record AreaDto(
    int Id,
    string Codigo,
    string Nombre,
    bool Activa,
    IReadOnlyList<SubAreaDto> SubAreas,
    IReadOnlyList<int> TipoIds,
    Guid? DuenoId = null,
    string? NombreDueno = null);

/// <summary>
/// Catálogo configurable (6.1.1): áreas, subáreas, tipos de documento y su asociación.
/// Las operaciones de escritura son solo del rol Administrador y quedan en la bitácora.
/// </summary>
public interface ICatalogoService
{
    Task<IReadOnlyList<AreaDto>> ListarAreasAsync(bool incluirInactivas = false, CancellationToken ct = default);

    Task<IReadOnlyList<TipoDocumentoDto>> ListarTiposAsync(bool incluirInactivos = false, CancellationToken ct = default);

    /// <summary>
    /// Tipos que se pueden crear en un área o subárea. Si la subárea no tiene tipos propios,
    /// hereda los de su área.
    /// </summary>
    Task<IReadOnlyList<TipoDocumentoDto>> TiposDisponiblesAsync(int areaId, int? subAreaId, CancellationToken ct = default);

    Task<int> CrearAreaAsync(Actor actor, string codigo, string nombre, CancellationToken ct = default);

    /// <summary>El código de un área no cambia: forma parte del código de sus documentos.</summary>
    Task ActualizarAreaAsync(Actor actor, int areaId, string nombre, bool activa, CancellationToken ct = default);

    /// <summary>
    /// Designa (o quita, con null) al dueño del área: una persona con rol Aprobador que aprueba las
    /// revisiones menores. Debe haber entrado al sistema para que se conozca su rol.
    /// </summary>
    Task EstablecerDuenoDeAreaAsync(Actor actor, int areaId, Guid? usuarioId, CancellationToken ct = default);

    /// <summary>
    /// Crea una subárea (la «unidad» del código de los documentos). Su abreviatura es única dentro del área y no cambia.
    /// </summary>
    Task<int> CrearSubAreaAsync(Actor actor, int areaId, string codigo, string nombre, CancellationToken ct = default);

    /// <summary>La abreviatura de una subárea no cambia: forma parte del código de sus documentos.</summary>
    Task ActualizarSubAreaAsync(Actor actor, int subAreaId, string nombre, bool activa, CancellationToken ct = default);

    /// <summary>
    /// Asigna la abreviatura a una subárea anterior al estándar de código (que no la tiene). Solo se puede hacer una vez.
    /// </summary>
    Task AsignarCodigoSubAreaAsync(Actor actor, int subAreaId, string codigo, CancellationToken ct = default);

    Task<int> CrearTipoAsync(Actor actor, string codigo, string nombre, int mesesRevision, CancellationToken ct = default);

    /// <summary>El código de un tipo no cambia: forma parte del código de sus documentos.</summary>
    Task ActualizarTipoAsync(Actor actor, int tipoId, string nombre, int mesesRevision, bool activo, CancellationToken ct = default);

    /// <summary>Reemplaza el conjunto de tipos asociados al área.</summary>
    Task EstablecerTiposDeAreaAsync(Actor actor, int areaId, IReadOnlyCollection<int> tipoIds, CancellationToken ct = default);

    /// <summary>Reemplaza el conjunto de tipos propios de la subárea. Vacío = hereda los del área.</summary>
    Task EstablecerTiposDeSubAreaAsync(Actor actor, int subAreaId, IReadOnlyCollection<int> tipoIds, CancellationToken ct = default);
}

public static class ReglasCatalogo
{
    public const int LongitudMinimaCodigo = 2;

    /// <summary>
    /// Los tipos documentales del estándar de Calidad tienen abreviaturas de una letra (P = Procedimiento, F = Formato,
    /// R = Registros), así que para ellos el mínimo es 1.
    /// </summary>
    public const int LongitudMinimaCodigoTipo = 1;

    /// <summary>
    /// Abreviatura de la unidad «General» de cada área: donde van los documentos de toda el área que no pertenecen a una
    /// subárea en particular (código <c>QRT-F-QA-GEN-001</c>). Es una subárea más, con las mismas reglas (inmutable).
    /// </summary>
    public const string UnidadGeneral = "GEN";

    public const string NombreUnidadGeneral = "General";

    public const int LongitudMaximaCodigo = 10;

    /// <summary>Normaliza un código (mayúsculas, sin espacios) y valida que solo tenga letras y dígitos.</summary>
    public static string NormalizarCodigo(string? codigo, string que, int longitudMinima = LongitudMinimaCodigo)
    {
        var limpio = (codigo ?? "").Trim().ToUpperInvariant();

        if (limpio.Length < longitudMinima || limpio.Length > LongitudMaximaCodigo
            || !limpio.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            throw new ReglaNegocioException(
                $"La abreviatura de {que} debe tener entre {longitudMinima} y {LongitudMaximaCodigo} caracteres, solo letras y números.");
        }

        return limpio;
    }

    public static string NormalizarNombre(string? nombre, string que, int maximo = 200)
    {
        var limpio = (nombre ?? "").Trim();
        if (limpio.Length == 0)
        {
            throw new ReglaNegocioException($"El nombre de {que} es obligatorio.");
        }

        if (limpio.Length > maximo)
        {
            throw new ReglaNegocioException($"El nombre de {que} no puede pasar de {maximo} caracteres.");
        }

        return limpio;
    }

    /// <summary>La subárea usa sus propios tipos; si no tiene ninguno, hereda los del área.</summary>
    public static IReadOnlyList<int> TiposEfectivos(IReadOnlyCollection<int> tiposDelArea, IReadOnlyCollection<int>? tiposDeLaSubArea) =>
        tiposDeLaSubArea is { Count: > 0 } ? [.. tiposDeLaSubArea] : [.. tiposDelArea];
}
