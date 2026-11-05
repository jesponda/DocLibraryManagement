using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;

namespace DocLibraryManagement.Seguridad;

public static class EndpointsArchivos
{
    /// <summary>
    /// Descarga de archivos de una revisión. Requiere sesión (política por defecto) y el servicio
    /// vuelve a comprobar permisos e integridad (hash) en cada descarga.
    /// </summary>
    public static void MapEndpointsArchivos(this WebApplication app)
    {
        app.MapGet("/archivos/{revisionId:int}/{tipo}", async (
            int revisionId, string tipo, HttpContext http, IRevisionService revisiones) =>
        {
            TipoArchivo? tipoArchivo = tipo.ToLowerInvariant() switch
            {
                "pdf" => TipoArchivo.Pdf,
                "fuente" => TipoArchivo.Fuente,
                _ => null,
            };

            if (tipoArchivo is null)
            {
                return Results.Text("Tipo de archivo no válido.", "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }

            try
            {
                var actor = Actor.Desde(http.User, http.Connection.RemoteIpAddress?.ToString());
                var archivo = await revisiones.AbrirArchivoAsync(actor, revisionId, tipoArchivo.Value, http.RequestAborted);

                // El navegador no debe adivinar el tipo: un PDF se muestra como PDF y nada más.
                http.Response.Headers.XContentTypeOptions = "nosniff";

                if (archivo.EnLinea)
                {
                    http.Response.Headers.ContentDisposition = $"inline; filename=\"{archivo.NombreDescarga}\"";
                    return Results.Stream(archivo.Contenido, archivo.TipoContenido, enableRangeProcessing: true);
                }

                return Results.File(archivo.Contenido, archivo.TipoContenido, archivo.NombreDescarga, enableRangeProcessing: true);
            }
            catch (AccesoDenegadoException ex)
            {
                // Con cuerpo, para que la re-ejecución de páginas de estado no lo reemplace por "no encontrado".
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ReglaNegocioException ex)
            {
                return Results.Text(ex.Message, "text/plain; charset=utf-8", statusCode: StatusCodes.Status404NotFound);
            }
        });
    }
}
