namespace DocLibraryManagement.Domain;

public enum TipoPaso
{
    Revision = 1,
    Aprobacion = 2,
}

public enum EstadoFlujo
{
    EnCurso = 1,
    Aprobado = 2,
    Rechazado = 3,
    Cancelado = 4,
}

public enum EstadoPaso
{
    Pendiente = 1,
    Aprobado = 2,
    Rechazado = 3,

    /// <summary>La etapa no llegó a ejecutarse porque el flujo terminó antes (rechazo o cancelación).</summary>
    Omitido = 4,
}

public static class TipoPasoExtensions
{
    /// <summary>Rol que debe tener quien decide una etapa de este tipo.</summary>
    public static RolSistema RolRequerido(this TipoPaso tipo) =>
        tipo == TipoPaso.Revision ? RolSistema.Revisor : RolSistema.Aprobador;

    /// <summary>Estado en que queda la revisión mientras la etapa vigente es de este tipo.</summary>
    public static EstadoRevision EstadoDeRevision(this TipoPaso tipo) =>
        tipo == TipoPaso.Revision ? EstadoRevision.EnRevision : EstadoRevision.EnAprobacion;
}

/// <summary>Etapa de la plantilla de flujo de un tipo de documento (7.2). El orden va de 1 en adelante.</summary>
public class PasoPlantilla
{
    public int Id { get; set; }

    public int TipoDocumentoId { get; set; }

    public TipoDocumento? TipoDocumento { get; set; }

    public int Orden { get; set; }

    public TipoPaso Clase { get; set; }

    public required string Nombre { get; set; }
}

/// <summary>
/// Una ejecución del flujo sobre una revisión. Al iniciar se COPIA la plantilla: cambiar la plantilla
/// después no afecta a los flujos ya iniciados. Si se rechaza y se reenvía, es una instancia nueva.
/// </summary>
public class InstanciaFlujo
{
    public int Id { get; set; }

    public int RevisionId { get; set; }

    public RevisionDocumento? Revision { get; set; }

    /// <summary>1, 2, 3… por revisión (cada reenvío suma uno).</summary>
    public int Numero { get; set; }

    public EstadoFlujo Estado { get; set; } = EstadoFlujo.EnCurso;

    public DateTime FechaInicioUtc { get; set; }

    public DateTime? FechaFinUtc { get; set; }

    public Guid IniciadoPorId { get; set; }

    public Usuario? IniciadoPor { get; set; }

    public List<PasoInstancia> Pasos { get; set; } = [];
}

public class PasoInstancia
{
    public int Id { get; set; }

    public int InstanciaFlujoId { get; set; }

    public InstanciaFlujo? Instancia { get; set; }

    public int Orden { get; set; }

    public TipoPaso Clase { get; set; }

    public required string Nombre { get; set; }

    public Guid AsignadoAId { get; set; }

    public Usuario? AsignadoA { get; set; }

    public EstadoPaso Estado { get; set; } = EstadoPaso.Pendiente;

    public Guid? DecididoPorId { get; set; }

    public Usuario? DecididoPor { get; set; }

    public DateTime? FechaDecisionUtc { get; set; }

    public string? Comentario { get; set; }
}
