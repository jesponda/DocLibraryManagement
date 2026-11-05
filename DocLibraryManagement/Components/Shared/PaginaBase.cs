using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Seguridad;
using Microsoft.AspNetCore.Components;

namespace DocLibraryManagement.Components.Shared;

/// <summary>
/// Base de las páginas que ejecutan operaciones de negocio: arma el Actor y convierte las
/// excepciones de regla de negocio en un mensaje para el usuario (sin exponer errores internos).
/// </summary>
public abstract class PaginaBase : ComponentBase
{
    [Inject] protected ProveedorActor ProveedorActor { get; set; } = default!;

    [Inject] protected ILogger<PaginaBase> Log { get; set; } = default!;

    protected string? Error { get; set; }

    protected string? Exito { get; set; }

    /// <summary>Ejecuta la acción; devuelve true si terminó sin error de negocio.</summary>
    protected async Task<bool> EjecutarAsync(Func<Actor, Task> accion, string? mensajeExito = null)
    {
        Error = null;
        Exito = null;

        try
        {
            await accion(await ProveedorActor.ObtenerAsync());
            Exito = mensajeExito;
            return true;
        }
        catch (ReglaNegocioException ex)
        {
            Error = ex.Message;
        }
        catch (AccesoDenegadoException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            // Un error inesperado se registra completo, pero al usuario solo se le dice lo necesario.
            Log.LogError(ex, "Error inesperado en {Pagina}", GetType().Name);
            Error = "Ocurrió un error inesperado. Si se repite, avisa a IT.";
        }

        return false;
    }

    protected static void Alternar(HashSet<int> conjunto, int id, bool marcado)
    {
        if (marcado)
        {
            conjunto.Add(id);
        }
        else
        {
            conjunto.Remove(id);
        }
    }
}
