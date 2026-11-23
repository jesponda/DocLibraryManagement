using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;

namespace DocLibraryManagement.Seguridad;

public static class EndpointsListaMaestra
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>
    /// Exporta la lista maestra con los mismos filtros de la pantalla. Requiere sesión (política por defecto);
    /// la lista es de lectura abierta, así que no se necesita más permiso.
    /// </summary>
    public static void MapEndpointsListaMaestra(this WebApplication app)
    {
        app.MapGet("/lista-maestra/exportar", async (
            string? formato, string? texto, string? q, int? area, int? subarea, int? tipo,
            IConsultaService consulta, IExportadorListaMaestra exportador, TimeProvider reloj, CancellationToken ct) =>
        {
            var f = (formato ?? "xlsx").ToLowerInvariant();
            if (f is not ("csv" or "xlsx"))
            {
                return Results.Text("Formato no válido. Usa csv o xlsx.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }

            IReadOnlyList<FilaListaMaestra> filas;
            try
            {
                filas = await consulta.ListaMaestraCompletaAsync(
                    new FiltroListaMaestra(string.IsNullOrWhiteSpace(texto) ? q : texto, area, subarea, tipo), ct);
            }
            catch (ReglaNegocioException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status400BadRequest);
            }

            var fecha = reloj.GetLocalNow().ToString("yyyyMMdd");
            return f == "csv"
                ? Results.File(exportador.ACsv(filas), "text/csv; charset=utf-8", $"ListaMaestra_{fecha}.csv")
                : Results.File(exportador.AXlsx(filas), TipoXlsx, $"ListaMaestra_{fecha}.xlsx");
        });
    }
}
