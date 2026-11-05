using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Flujo;

public sealed record PasoPlantillaDto(int Orden, TipoPaso Clase, string Nombre);

public sealed record CandidatoDto(Guid UsuarioId, string Nombre, string Identificador);

public sealed record AsignacionPaso(int Orden, Guid UsuarioId);

public sealed record PasoInstanciaDto(
    int Id,
    int Orden,
    TipoPaso Clase,
    string Nombre,
    Guid AsignadoAId,
    string AsignadoA,
    EstadoPaso Estado,
    string? DecididoPor,
    DateTime? FechaDecisionUtc,
    string? Comentario);

public sealed record InstanciaFlujoDto(
    int Id,
    int Numero,
    EstadoFlujo Estado,
    DateTime FechaInicioUtc,
    DateTime? FechaFinUtc,
    string IniciadoPor,
    IReadOnlyList<PasoInstanciaDto> Pasos);

public sealed record TareaPendienteDto(
    int PasoId,
    int RevisionId,
    int DocumentoId,
    string Codigo,
    string Titulo,
    string Etiqueta,
    TipoPaso Clase,
    string NombrePaso,
    DateTime DesdeUtc);

public interface IFlujoService
{
    Task<IReadOnlyList<PasoPlantillaDto>> ObtenerPlantillaAsync(int tipoDocumentoId, CancellationToken ct = default);

    /// <summary>Reemplaza la plantilla del tipo. Solo Administrador. No afecta a flujos ya iniciados.</summary>
    Task GuardarPlantillaAsync(Actor actor, int tipoDocumentoId, IReadOnlyList<PasoPlantillaDto> pasos, CancellationToken ct = default);

    /// <summary>
    /// Personas que pueden decidir una etapa de este tipo. Sale de la copia de roles del último acceso:
    /// solo aparecen quienes ya entraron al sistema. La validación real se repite al decidir.
    /// </summary>
    Task<IReadOnlyList<CandidatoDto>> CandidatosAsync(TipoPaso clase, CancellationToken ct = default);

    /// <summary>Envía el borrador a revisión: copia la plantilla y asigna una persona a cada etapa.</summary>
    Task IniciarAsync(Actor actor, int revisionId, IReadOnlyList<AsignacionPaso> asignaciones, CancellationToken ct = default);

    /// <summary>Flujos de la revisión, del más reciente al más antiguo.</summary>
    Task<IReadOnlyList<InstanciaFlujoDto>> HistorialAsync(Actor actor, int revisionId, CancellationToken ct = default);

    /// <summary>Etapas que esperan la decisión del actor ahora mismo.</summary>
    Task<IReadOnlyList<TareaPendienteDto>> MisTareasAsync(Actor actor, CancellationToken ct = default);

    Task DecidirAsync(Actor actor, int pasoId, bool aprobar, string? comentario, CancellationToken ct = default);

    Task ReasignarAsync(Actor actor, int pasoId, Guid nuevoUsuarioId, string motivo, CancellationToken ct = default);

    /// <summary>Retira la revisión del flujo y la devuelve a Borrador.</summary>
    Task CancelarFlujoAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default);
}

public static class ReglasFlujo
{
    public const int MaximoPasos = 10;
    public const int MaximoNombre = 100;
    public const int MaximoComentario = 2000;

    /// <summary>
    /// Valida y normaliza la plantilla: el orden lo da la posición en la lista (1, 2, 3…), el nombre
    /// por defecto es la clase, debe haber al menos una aprobación y la ÚLTIMA etapa debe ser una aprobación
    /// (la decisión final nunca es una revisión).
    /// </summary>
    public static IReadOnlyList<PasoPlantillaDto> NormalizarPlantilla(IReadOnlyList<PasoPlantillaDto> pasos)
    {
        if (pasos.Count == 0)
        {
            throw new ReglaNegocioException("El flujo necesita al menos una etapa.");
        }

        if (pasos.Count > MaximoPasos)
        {
            throw new ReglaNegocioException($"El flujo no puede tener más de {MaximoPasos} etapas.");
        }

        var resultado = new List<PasoPlantillaDto>(pasos.Count);
        for (var i = 0; i < pasos.Count; i++)
        {
            var paso = pasos[i];
            if (!Enum.IsDefined(paso.Clase))
            {
                throw new ReglaNegocioException("Una de las etapas tiene un tipo no válido.");
            }

            var nombre = (paso.Nombre ?? "").Trim();
            if (nombre.Length == 0)
            {
                nombre = paso.Clase == TipoPaso.Revision ? "Revisión" : "Aprobación";
            }

            if (nombre.Length > MaximoNombre)
            {
                throw new ReglaNegocioException($"El nombre de una etapa no puede pasar de {MaximoNombre} caracteres.");
            }

            resultado.Add(new PasoPlantillaDto(i + 1, paso.Clase, nombre));
        }

        if (resultado[^1].Clase != TipoPaso.Aprobacion)
        {
            throw new ReglaNegocioException("La última etapa del flujo debe ser una aprobación.");
        }

        return resultado;
    }

    public static string NormalizarComentario(string? comentario, bool obligatorio, string mensajeObligatorio)
    {
        var limpio = (comentario ?? "").Trim();
        if (obligatorio && limpio.Length == 0)
        {
            throw new ReglaNegocioException(mensajeObligatorio);
        }

        if (limpio.Length > MaximoComentario)
        {
            throw new ReglaNegocioException($"El comentario no puede pasar de {MaximoComentario} caracteres.");
        }

        return limpio;
    }
}
