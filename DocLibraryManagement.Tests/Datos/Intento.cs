namespace DocLibraryManagement.Tests.Datos;

/// <summary>Resultado de intentar una operación: sirve para lanzar varias a la vez y revisar cuáles pasaron.</summary>
public sealed record Intento(bool Exito, Exception? Error)
{
    public static async Task<Intento> DeAsync(Func<Task> accion)
    {
        try
        {
            await accion();
            return new Intento(true, null);
        }
        catch (Exception ex)
        {
            return new Intento(false, ex);
        }
    }
}
