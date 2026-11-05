using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Catalogo;

public sealed record TipoDocumentoDto(int Id, string Codigo, string Nombre, int MesesRevision, bool Activo);

public sealed record SubAreaDto(int Id, int AreaId, string Nombre, bool Activa, IReadOnlyList<int> TipoIds);

public sealed record AreaDto(
    int Id,
    string Codigo,
    string Nombre,
    bool Activa,
    IReadOnlyList<SubAreaDto> SubAreas,
    IReadOnlyList<int> TipoIds);

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

    Task<int> CrearSubAreaAsync(Actor actor, int areaId, string nombre, CancellationToken ct = default);

    Task ActualizarSubAreaAsync(Actor actor, int subAreaId, string nombre, bool activa, CancellationToken ct = default);

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
    public const int LongitudMaximaCodigo = 10;

    /// <summary>Normaliza un código (mayúsculas, sin espacios) y valida que solo tenga letras y dígitos.</summary>
    public static string NormalizarCodigo(string? codigo, string que)
    {
        var limpio = (codigo ?? "").Trim().ToUpperInvariant();

        if (limpio.Length is < LongitudMinimaCodigo or > LongitudMaximaCodigo
            || !limpio.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            throw new ReglaNegocioException(
                $"La abreviatura de {que} debe tener entre {LongitudMinimaCodigo} y {LongitudMaximaCodigo} caracteres, solo letras y números.");
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
