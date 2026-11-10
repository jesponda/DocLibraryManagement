namespace DocLibraryManagement.Domain;

public enum EstadoNotificacion
{
    /// <summary>Esperando su turno de envío (o su próximo reintento).</summary>
    Pendiente = 1,

    /// <summary>
    /// Un proceso la tomó y la está enviando. Tiene un «arriendo» con vencimiento: si el proceso muere a mitad del
    /// envío, pasado el plazo vuelve a poder tomarse. Evita que dos procesos manden el mismo correo.
    /// </summary>
    Procesando = 2,

    Enviado = 3,

    /// <summary>Agotó sus reintentos. Un Administrador puede reintentarla desde la pantalla de la cola.</summary>
    Error = 4,
}

/// <summary>
/// Un correo en cola (7.8). Se crea en la MISMA transacción que el cambio que lo origina: si el cambio se
/// deshace no sale correo, y nunca se envía desde el hilo de la petición.
/// </summary>
public class Notificacion
{
    public long Id { get; set; }

    /// <summary>Evento que lo originó (p. ej. «TareaAsignada»). Sirve para filtrar y para depurar.</summary>
    public required string Tipo { get; set; }

    public Guid DestinatarioId { get; set; }

    public Usuario? Destinatario { get; set; }

    /// <summary>Ya renderizado: editar una plantilla después no cambia lo que ya está en cola.</summary>
    public required string Asunto { get; set; }

    public required string Cuerpo { get; set; }

    public string? CodigoDocumento { get; set; }

    public EstadoNotificacion Estado { get; set; } = EstadoNotificacion.Pendiente;

    public int Intentos { get; set; }

    public DateTime FechaCreacionUtc { get; set; }

    /// <summary>Cuándo vuelve a ser elegible: el siguiente reintento, o el vencimiento del arriendo si está Procesando.</summary>
    public DateTime ProximoIntentoUtc { get; set; }

    public DateTime? FechaEnvioUtc { get; set; }

    public string? UltimoError { get; set; }

    /// <summary>
    /// Clave de idempotencia opcional. Dos notificaciones con la misma clave no pueden coexistir: así un aviso
    /// periódico (p. ej. «vence en 30 días») nunca se manda dos veces aunque la tarea corra repetida.
    /// </summary>
    public string? Clave { get; set; }

    /// <summary>Evita que dos procesos tomen la misma notificación a la vez.</summary>
    public byte[]? RowVersion { get; set; }
}
