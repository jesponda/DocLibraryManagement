namespace DocLibraryManagement.Domain;

public enum EstadoSolicitudSinCambios
{
    /// <summary>Esperando la decisión del dueño del área.</summary>
    Pendiente = 1,

    /// <summary>El dueño la aprobó: la fecha de próxima revisión se extendió.</summary>
    Aprobada = 2,

    /// <summary>El dueño la rechazó: la fecha no cambia; hay que abrir una revisión nueva.</summary>
    Rechazada = 3,

    /// <summary>La retiró quien la propuso, o dejó de aplicar (la revisión ya no es la vigente).</summary>
    Cancelada = 4,
}

/// <summary>
/// «Revisado sin cambios» (7.4): el responsable declara que revisó el documento vigente y no necesita cambios, y el
/// dueño del área lo confirma. Aprobarla extiende la fecha de próxima revisión sin crear una revisión nueva.
/// </summary>
public class SolicitudSinCambios
{
    public int Id { get; set; }

    /// <summary>La revisión VIGENTE que se declara revisada.</summary>
    public int RevisionId { get; set; }

    public RevisionDocumento? Revision { get; set; }

    public Guid SolicitanteId { get; set; }

    public Usuario? Solicitante { get; set; }

    public required string Comentario { get; set; }

    public EstadoSolicitudSinCambios Estado { get; set; } = EstadoSolicitudSinCambios.Pendiente;

    public DateTime FechaSolicitudUtc { get; set; }

    /// <summary>La fecha de próxima revisión que tenía la revisión al proponer (para la trazabilidad).</summary>
    public DateTime FechaProximaAnteriorUtc { get; set; }

    /// <summary>La fecha que quedó tras aprobarla. Nula mientras no se apruebe.</summary>
    public DateTime? FechaProximaNuevaUtc { get; set; }

    public Guid? DecididaPorId { get; set; }

    public Usuario? DecididaPor { get; set; }

    public string? ComentarioDecision { get; set; }

    public DateTime? FechaDecisionUtc { get; set; }

    /// <summary>Evita que dos decisiones simultáneas se pisen.</summary>
    public byte[]? RowVersion { get; set; }
}
