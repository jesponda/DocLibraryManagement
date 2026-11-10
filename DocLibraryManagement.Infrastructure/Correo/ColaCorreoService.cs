using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Correo;

internal sealed class ColaCorreoService(
    IDbContextFactory<AppDbContext> fabrica,
    ICorreoSender sender,
    IOptions<CorreoOptions> opciones,
    TimeProvider reloj,
    ILogger<ColaCorreoService> log) : IColaCorreoService
{
    private const int MaximoLongitudError = 1000;

    public async Task<int> ProcesarPendientesAsync(int maximo = 50, CancellationToken ct = default)
    {
        if (opciones.Value.Modo == ModoCorreo.Desactivado)
        {
            return 0;
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        List<long> ids;
        await using (var consulta = await fabrica.CreateDbContextAsync(ct))
        {
            // «Procesando» con el arriendo vencido cuenta como pendiente: el proceso que la tomó murió a mitad del envío.
            ids = await consulta.Notificaciones.AsNoTracking()
                .Where(n => (n.Estado == EstadoNotificacion.Pendiente || n.Estado == EstadoNotificacion.Procesando)
                            && n.ProximoIntentoUtc <= ahora)
                .OrderBy(n => n.Id)
                .Select(n => n.Id)
                .Take(maximo)
                .ToListAsync(ct);
        }

        var enviadas = 0;
        foreach (var id in ids)
        {
            try
            {
                if (await EnviarUnaAsync(id, ct))
                {
                    enviadas++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Falló el procesamiento de la notificación {Id}", id);
            }
        }

        return enviadas;
    }

    private async Task<bool> EnviarUnaAsync(long id, CancellationToken ct)
    {
        var o = opciones.Value;
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var n = await db.Notificaciones.Include(x => x.Destinatario).FirstOrDefaultAsync(x => x.Id == id, ct);
        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (n is null
            || n.Estado is not (EstadoNotificacion.Pendiente or EstadoNotificacion.Procesando)
            || n.ProximoIntentoUtc > ahora)
        {
            return false;
        }

        // 1) Reservarla ANTES de enviar. El RowVersion garantiza que, si dos procesos la ven a la vez, solo uno la reserva.
        n.Estado = EstadoNotificacion.Procesando;
        n.ProximoIntentoUtc = ahora.AddMinutes(Math.Max(1, o.MinutosArriendo));
        n.Intentos++;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // otro proceso se adelantó
        }

        // 2) Enviar. La dirección se resuelve AHORA, no al encolar: si el Administrador corrige un correo, el reintento lo usa.
        var destino = ResolutorCorreo.Efectivo(n.Destinatario!.Correo, n.Destinatario.Identificador, o.PatronCorreo);
        string? error = null;
        if (destino.Direccion is null)
        {
            error = "El destinatario no tiene correo: ni uno capturado ni uno por convención.";
        }
        else
        {
            try
            {
                await sender.EnviarAsync(new MensajeCorreo(destino.Direccion, n.Destinatario.NombreVisible, n.Asunto, n.Cuerpo), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = Resumir(ex);
                log.LogWarning(ex, "No se pudo enviar la notificación {Id} ({Tipo}) a {Destino}", n.Id, n.Tipo, destino.Direccion);
            }
        }

        // 3) Registrar el resultado.
        var fin = reloj.GetUtcNow().UtcDateTime;
        if (error is null)
        {
            n.Estado = EstadoNotificacion.Enviado;
            n.FechaEnvioUtc = fin;
            n.UltimoError = null;
        }
        else
        {
            n.UltimoError = error;
            if (n.Intentos >= o.MaximoIntentos)
            {
                n.Estado = EstadoNotificacion.Error;
            }
            else
            {
                n.Estado = EstadoNotificacion.Pendiente;
                // Espera exponencial: 5, 10, 20, 40… minutos (con la base configurada).
                n.ProximoIntentoUtc = fin.AddMinutes(Math.Max(1, o.MinutosReintento) * Math.Pow(2, n.Intentos - 1));
            }
        }

        await db.SaveChangesAsync(ct);
        return error is null;
    }

    private static string Resumir(Exception ex)
    {
        var texto = $"{ex.GetType().Name}: {ex.Message}".Replace('\r', ' ').Replace('\n', ' ');
        return texto.Length <= MaximoLongitudError ? texto : texto[..(MaximoLongitudError - 1)] + "…";
    }

    // ---------- Administración ----------

    public async Task<Pagina<NotificacionDto>> ListarAsync(Actor actor, EstadoNotificacion? estado, int pagina = 1, int tamano = 25, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var (numero, tam) = ReglasBusqueda.NormalizarPagina(pagina, tamano, 25);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var consulta = db.Notificaciones.AsNoTracking();
        if (estado is { } e)
        {
            consulta = consulta.Where(n => n.Estado == e);
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(n => n.Id)
            .Skip((numero - 1) * tam).Take(tam)
            .Select(n => new
            {
                n.Id, n.Tipo, Destinatario = n.Destinatario!.NombreVisible, n.Destinatario.Identificador, n.Destinatario.Correo,
                n.Asunto, n.CodigoDocumento, n.Estado, n.Intentos, n.FechaCreacionUtc, n.FechaEnvioUtc, n.ProximoIntentoUtc, n.UltimoError,
            })
            .ToListAsync(ct);

        var patron = opciones.Value.PatronCorreo;
        var elementos = filas.Select(f => new NotificacionDto(
            f.Id, f.Tipo, f.Destinatario, ResolutorCorreo.Efectivo(f.Correo, f.Identificador, patron).Direccion,
            f.Asunto, f.CodigoDocumento, f.Estado, f.Intentos, f.FechaCreacionUtc, f.FechaEnvioUtc, f.ProximoIntentoUtc, f.UltimoError)).ToList();

        return new Pagina<NotificacionDto>(elementos, numero, tam, total);
    }

    public async Task ReintentarAsync(Actor actor, long notificacionId, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var n = await db.Notificaciones.FirstOrDefaultAsync(x => x.Id == notificacionId, ct)
            ?? throw new ReglaNegocioException("La notificación no existe.");

        if (n.Estado != EstadoNotificacion.Error)
        {
            throw new ReglaNegocioException("Solo se pueden reintentar las notificaciones en error.");
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        n.Estado = EstadoNotificacion.Pendiente;
        n.Intentos = 0;
        n.ProximoIntentoUtc = ahora;
        Bitacora.Registrar(db, reloj, actor, "Correo.Reintentar", nameof(Notificacion), n.Id.ToString(),
            anterior: $"Error: {n.UltimoError}", nuevo: "Pendiente", comentario: n.Asunto);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ReglaNegocioException("La notificación cambió mientras la reintentabas. Recarga la página.");
        }
    }

    private static void ExigirAdministrador(Actor actor)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede ver o gestionar la cola de correo.");
        }
    }
}
