namespace DocLibraryManagement.Application.Documentos;

public interface IPublicacionService
{
    /// <summary>
    /// Publica las revisiones aprobadas cuya fecha de entrada en vigor ya llegó (vigencia diferida).
    /// Cada una va en su propia transacción: si una falla, las demás se publican igual.
    /// Devuelve cuántas se publicaron. Es idempotente: ejecutarla dos veces seguidas no repite nada.
    /// </summary>
    Task<int> ActivarPendientesAsync(CancellationToken ct = default);
}
