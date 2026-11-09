using DocLibraryManagement.Application.Documentos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Documentos;

/// <summary>Opciones de la sección "Publicacion" de appsettings.</summary>
public class PublicacionOptions
{
    public const string Seccion = "Publicacion";

    /// <summary>Cada cuántos minutos se buscan revisiones aprobadas cuya fecha de vigencia ya llegó. 0 = desactivado.</summary>
    public int MinutosEntreRevisiones { get; set; } = 5;
}

/// <summary>
/// Pone en vigor las revisiones aprobadas con fecha futura cuando ésta llega. La lógica vive en
/// <see cref="IPublicacionService"/> (probada aparte); este proceso solo la dispara con un temporizador.
/// </summary>
internal sealed class ActivadorDeVigencias(
    IServiceScopeFactory ambitos,
    IOptions<PublicacionOptions> opciones,
    ILogger<ActivadorDeVigencias> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var minutos = opciones.Value.MinutosEntreRevisiones;
        if (minutos <= 0)
        {
            log.LogInformation("Activación automática de vigencias desactivada (Publicacion:MinutosEntreRevisiones = 0).");
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromMinutes(minutos));
        do
        {
            try
            {
                using var ambito = ambitos.CreateScope();
                var publicadas = await ambito.ServiceProvider.GetRequiredService<IPublicacionService>().ActivarPendientesAsync(ct);
                if (publicadas > 0)
                {
                    log.LogInformation("Se pusieron en vigor {Cantidad} revisión(es) programada(s).", publicadas);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un fallo (p. ej. BD caída un momento) no debe matar el proceso: se reintenta en la siguiente vuelta.
                log.LogError(ex, "Falló la activación automática de vigencias.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(ct));
    }
}
