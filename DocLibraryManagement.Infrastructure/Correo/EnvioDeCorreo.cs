using DocLibraryManagement.Application.Correo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Correo;

/// <summary>
/// Vacía la cola de correo con un temporizador. Va aparte de <c>TareasDeFondo</c> porque el correo debe salir en
/// segundos, no cada cinco minutos. La lógica (arriendo, reintentos) vive en <see cref="IColaCorreoService"/>.
/// </summary>
internal sealed class EnvioDeCorreo(
    IServiceScopeFactory ambitos,
    IOptions<CorreoOptions> opciones,
    ILogger<EnvioDeCorreo> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = opciones.Value;
        if (o.Modo == ModoCorreo.Desactivado || o.SegundosEntreEnvios <= 0)
        {
            log.LogInformation("Envío de correo desactivado (Correo:Modo = {Modo}, SegundosEntreEnvios = {Seg}).", o.Modo, o.SegundosEntreEnvios);
            return;
        }

        using var temporizador = new PeriodicTimer(TimeSpan.FromSeconds(o.SegundosEntreEnvios));
        do
        {
            try
            {
                using var ambito = ambitos.CreateScope();
                var enviadas = await ambito.ServiceProvider.GetRequiredService<IColaCorreoService>().ProcesarPendientesAsync(ct: ct);
                if (enviadas > 0)
                {
                    log.LogInformation("Se enviaron {Cantidad} correo(s).", enviadas);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un fallo (BD caída un momento, SMTP inalcanzable) no debe matar el proceso: se reintenta en la siguiente vuelta.
                log.LogError(ex, "Falló el envío de correo.");
            }
        }
        while (await temporizador.WaitForNextTickAsync(ct));
    }
}
