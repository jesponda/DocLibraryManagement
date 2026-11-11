using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Exportaciones (CSV y Excel) de los reportes y de la bitácora, con los mismos filtros que las pantallas. Requieren
/// sesión (política por defecto) y cada servicio vuelve a validar el permiso con el <see cref="Actor"/>: pedir la URL a
/// mano no da más de lo que la pantalla permite.
/// </summary>
public static class EndpointsReportes
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void MapEndpointsReportes(this WebApplication app)
    {
        app.MapGet("/reportes/vencimientos/exportar", (
            HttpContext http, IReportesService reportes, IExportadorTablas exportador, TimeProvider reloj,
            string? formato, int? estado, int? area, int? tipo, string? texto) =>
            Exportar(http, reloj, formato, "Vencimientos", async ct =>
            {
                if (estado is { } e && !Enum.IsDefined((FiltroEstadoVencimiento)e))
                {
                    throw new ReglaNegocioException("El estado pedido no es válido.");
                }

                var filtro = new FiltroVencimientos(estado is { } v ? (FiltroEstadoVencimiento)v : FiltroEstadoVencimiento.ConAviso, area, tipo, texto);
                return TablasReporte.Vencimientos(await reportes.VencimientosCompletoAsync(ActorDe(http), filtro, ct));
            }, exportador));

        app.MapGet("/reportes/tareas/exportar", (
            HttpContext http, IReportesService reportes, IExportadorTablas exportador, TimeProvider reloj,
            string? formato, Guid? usuario, int? tipo, int? area) =>
            Exportar(http, reloj, formato, "TareasPendientes", async ct =>
            {
                if (tipo is { } t && !Enum.IsDefined((TipoTareaReporte)t))
                {
                    throw new ReglaNegocioException("El tipo de tarea pedido no es válido.");
                }

                var filtro = new FiltroTareas(usuario, tipo is { } v ? (TipoTareaReporte)v : null, area);
                return TablasReporte.Tareas((await reportes.TareasAsync(ActorDe(http), filtro, ct)).Filas);
            }, exportador));

        app.MapGet("/auditoria/exportar", (
            HttpContext http, IAuditoriaService auditoria, IExportadorTablas exportador, TimeProvider reloj,
            string? formato, DateOnly? desde, DateOnly? hasta, string? usuario, string? accion, string? entidad, string? identificador, string? texto) =>
            Exportar(http, reloj, formato, "Bitacora", async ct =>
            {
                var (desdeUtc, hastaUtc) = ReglasReporte.RangoUtc(desde, hasta, reloj.LocalTimeZone);
                var filtro = new FiltroAuditoria(desdeUtc, hastaUtc, usuario, accion, entidad, identificador, texto);
                return TablasReporte.Auditoria(await auditoria.ExportarAsync(ActorDe(http), filtro, ct));
            }, exportador));

        app.MapGet("/documentos/{id:int}/historial/exportar", (
            int id, HttpContext http, IReportesService reportes, IExportadorTablas exportador, TimeProvider reloj, string? formato) =>
            Exportar(http, reloj, formato, $"Historial_{id}", async ct =>
                TablasReporte.Historial(await reportes.HistorialDocumentoAsync(ActorDe(http), id, ct)), exportador));
    }

    private static Actor ActorDe(HttpContext http) =>
        Actor.Desde(http.User, http.Connection.RemoteIpAddress?.ToString());

    private static async Task<IResult> Exportar(
        HttpContext http, TimeProvider reloj, string? formato, string nombreBase,
        Func<CancellationToken, Task<TablaExportable>> construir, IExportadorTablas exportador)
    {
        var f = (formato ?? "xlsx").ToLowerInvariant();
        if (f is not ("csv" or "xlsx"))
        {
            return Texto("Formato no válido. Usa csv o xlsx.", StatusCodes.Status400BadRequest);
        }

        try
        {
            var tabla = await construir(http.RequestAborted);
            var fecha = reloj.GetLocalNow().ToString("yyyyMMdd");
            http.Response.Headers.XContentTypeOptions = "nosniff";

            return f == "csv"
                ? Results.File(exportador.ACsv(tabla), "text/csv; charset=utf-8", $"{nombreBase}_{fecha}.csv")
                : Results.File(exportador.AXlsx(tabla), TipoXlsx, $"{nombreBase}_{fecha}.xlsx");
        }
        catch (AccesoDenegadoException ex)
        {
            // Con cuerpo, para que la re-ejecución de páginas de estado no lo reemplace por «no encontrado».
            return Texto(ex.Message, StatusCodes.Status403Forbidden);
        }
        catch (ReglaNegocioException ex)
        {
            return Texto(ex.Message, StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Texto(string mensaje, int estado) =>
        Results.Text(mensaje, "text/plain; charset=utf-8", statusCode: estado);
}
