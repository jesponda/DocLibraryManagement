using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Documentos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Documentos;

/// <summary>Opciones de la sección "TareasDeFondo" de appsettings.</summary>
public class TareasDeFondoOptions
{
    public const string Seccion = "TareasDeFondo";

    /// <summary>Cada cuántos minutos corren las tareas de fondo. 0 = desactivadas.</summary>
    public int MinutosEntreEjecuciones { get; set; } = 5;
}

/// <summary>
/// Dispara con un temporizador el trabajo que no depende de que alguien haga clic:
/// poner en vigor las revisiones aprobadas con fecha futura y leer el texto de los PDF ya publicados que
/// aún no se han indexado. La lógica vive en los servicios (probados aparte); aquí solo se llaman.
/// </summary>
internal sealed class TareasDeFondo(
    IServiceScopeFactory ambitos,
    IOptions<TareasDeFondoOptions> opciones,
    ILogger<TareasDeFondo> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var minutos = opciones.Value.MinutosEntreEjecuciones;
        if (minutos <= 0)
        {
            log.LogInformation("Tareas de fondo desactivadas (TareasDeFondo:MinutosEntreEjecuciones = 0).");
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
        do
        {
            // Cada tarea va aparte: que falle una no impide la otra.
            await EjecutarAsync("activación de vigencias", async sp =>
            {
                var n = await sp.GetRequiredService<IPublicacionService>().ActivarPendientesAsync(ct);
                if (n > 0)
                {
                    log.LogInformation("Se pusieron en vigor {Cantidad} revisión(es) programada(s).", n);
                }
            });

            await EjecutarAsync("indexado de texto", async sp =>
            {
                var n = await sp.GetRequiredService<IIndexadorService>().IndexarPendientesAsync(ct: ct);
                if (n > 0)
                {
                    log.LogInformation("Se indexó el texto de {Cantidad} PDF(s).", n);
                }
            });
        }
        while (await temporizador.WaitForNextTickAsync(ct));
    }

    private async Task EjecutarAsync(string nombre, Func<IServiceProvider, Task> tarea)
    {
        try
        {
            using var ambito = ambitos.CreateScope();
            await tarea(ambito.ServiceProvider);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un fallo (p. ej. BD caída un momento) no debe matar el proceso: se reintenta en la siguiente vuelta.
            log.LogError(ex, "Falló la tarea de fondo: {Tarea}", nombre);
        }
    }
}
