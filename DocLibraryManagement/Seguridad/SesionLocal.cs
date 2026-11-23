using System.Security.Claims;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Sesión del modo «Local». La cookie solo prueba QUIÉN eres; los roles, las áreas y si la cuenta sigue activa se leen de la
/// base de datos en cada solicitud. Así, desactivar a alguien o quitarle un rol surte efecto de inmediato, sin esperar a que
/// caduque la cookie.
/// </summary>
public static class SesionLocal
{
    public const string ClaimSello = "doclibrary/sello";
    public const string ClaimCambiarContrasena = "doclibrary/cambiar-contrasena";

    public static ClaimsPrincipal Construir(AccesoCuenta acceso)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, acceso.Identificador),
            new(ClaimTypes.NameIdentifier, acceso.Id.ToString()),
            new(ClaimSello, acceso.Sello.ToString()),
        };
        claims.AddRange(acceso.Roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        claims.AddRange(acceso.Areas.Select(a => new Claim(ClaimsDoc.Area, a)));
        if (acceso.DebeCambiarContrasena)
        {
            claims.Add(new Claim(ClaimCambiarContrasena, "1"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    public static async Task IniciarAsync(HttpContext http, AccesoCuenta acceso) =>
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, Construir(acceso),
            new AuthenticationProperties { IsPersistent = false });

    public static async Task ValidarAsync(CookieValidatePrincipalContext contexto)
    {
        var nombre = contexto.Principal?.Identity?.Name;
        var sello = contexto.Principal?.FindFirstValue(ClaimSello);
        if (string.IsNullOrEmpty(nombre) || string.IsNullOrEmpty(sello))
        {
            await Rechazar(contexto);
            return;
        }

        var autenticacion = contexto.HttpContext.RequestServices.GetRequiredService<IAutenticacionLocal>();
        var acceso = await autenticacion.ObtenerAccesoAsync(nombre, contexto.HttpContext.RequestAborted);

        // Cuenta inexistente, desactivada, o con la contraseña/estado cambiados desde otra sesión: la cookie ya no vale.
        if (acceso is null || !acceso.Activa || !string.Equals(acceso.Sello.ToString(), sello, StringComparison.OrdinalIgnoreCase))
        {
            await Rechazar(contexto);
            return;
        }

        contexto.ReplacePrincipal(Construir(acceso));
    }

    private static Task Rechazar(CookieValidatePrincipalContext contexto)
    {
        // Solo se rechaza esta solicitud; NO se borra la cookie. Si se borrara, una solicitud rezagada con la cookie vieja
        // (por ejemplo, un archivo que la página aún cargaba al cambiar la contraseña) podría borrar la cookie nueva que
        // acaba de emitirse en otra respuesta y dejar fuera a quien acaba de entrar.
        contexto.RejectPrincipal();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Redirige a «cambiar contraseña» a quien entró con una temporal, hasta que la cambie. No se le deja usar nada más.
    /// </summary>
    public static IApplicationBuilder UseCambioDeContrasenaObligatorio(this IApplicationBuilder app) =>
        app.Use(async (http, siguiente) =>
        {
            if (http.User.Identity?.IsAuthenticated == true
                && http.User.HasClaim(c => c.Type == ClaimCambiarContrasena)
                && !EsRutaPermitida(http.Request.Path))
            {
                http.Response.Redirect($"{http.Request.PathBase}/cuenta/contrasena");
                return;
            }

            await siguiente();
        });

    private static bool EsRutaPermitida(PathString ruta) =>
        ruta.StartsWithSegments("/cuenta/contrasena")
        || ruta.StartsWithSegments("/acceso")
        || ruta.StartsWithSegments("/health")
        || ruta.StartsWithSegments("/_framework")
        || ruta.StartsWithSegments("/_blazor")
        || Path.HasExtension(ruta.Value); // CSS, JS, imágenes: la pantalla de cambio las necesita

    // ---------- mensajes en la URL ----------

    /// <summary>Protege un mensaje para llevarlo en la dirección sin que nadie pueda inventar el texto que se muestra.</summary>
    public static string Proteger(IDataProtectionProvider proveedor, string mensaje) =>
        proveedor.CreateProtector("sgd.mensajes").ToTimeLimitedDataProtector().Protect(mensaje, TimeSpan.FromMinutes(5));

    public static string? Leer(IDataProtectionProvider proveedor, string? protegido)
    {
        if (string.IsNullOrEmpty(protegido))
        {
            return null;
        }

        try
        {
            return proveedor.CreateProtector("sgd.mensajes").ToTimeLimitedDataProtector().Unprotect(protegido);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public static class EndpointsCuentas
{
    public static void MapEndpointsCuentas(this WebApplication app)
    {
        app.MapPost("/acceso/entrar", async (
            [Microsoft.AspNetCore.Mvc.FromForm] string? usuario,
            [Microsoft.AspNetCore.Mvc.FromForm] string? contrasena,
            [Microsoft.AspNetCore.Mvc.FromForm] string? retorno,
            HttpContext http,
            IAutenticacionLocal autenticacion) =>
        {
            var respuesta = await autenticacion.IniciarSesionAsync(usuario ?? "", contrasena ?? "",
                http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);

            if (respuesta.Resultado != ResultadoInicioSesion.Correcto || respuesta.Cuenta is null)
            {
                var codigo = respuesta.Resultado == ResultadoInicioSesion.Bloqueado ? "bloqueo" : "rechazo";
                var destino = $"~/acceso?e={codigo}" + (EsLocal(retorno) ? $"&retorno={Uri.EscapeDataString(retorno!)}" : "");
                return Results.LocalRedirect(destino);
            }

            await SesionLocal.IniciarAsync(http, respuesta.Cuenta);
            return respuesta.Cuenta.DebeCambiarContrasena
                ? Results.LocalRedirect("~/cuenta/contrasena")
                : Results.LocalRedirect(EsLocal(retorno) ? retorno! : "~/");
        }).AllowAnonymous();

        app.MapPost("/acceso/salir", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("~/acceso");
        }).AllowAnonymous();

        app.MapPost("/cuenta/contrasena/cambiar", async (
            [Microsoft.AspNetCore.Mvc.FromForm] string? actual,
            [Microsoft.AspNetCore.Mvc.FromForm] string? nueva,
            [Microsoft.AspNetCore.Mvc.FromForm] string? confirmar,
            HttpContext http,
            IAutenticacionLocal autenticacion,
            IDataProtectionProvider proteccion) =>
        {
            string Volver(string mensaje) =>
                $"~/cuenta/contrasena?m={Uri.EscapeDataString(SesionLocal.Proteger(proteccion, mensaje))}";

            if (nueva != confirmar)
            {
                return Results.LocalRedirect(Volver("La contraseña nueva y su confirmación no coinciden."));
            }

            try
            {
                var acceso = await autenticacion.CambiarContrasenaAsync(http.User.Identity!.Name!, actual ?? "", nueva ?? "",
                    http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
                await SesionLocal.IniciarAsync(http, acceso); // el sello cambió: se renueva esta sesión; las demás se cierran
                return Results.LocalRedirect("~/mi-acceso?cambiada=1");
            }
            catch (ReglaNegocioException ex)
            {
                return Results.LocalRedirect(Volver(ex.Message));
            }
            catch (AccesoDenegadoException ex)
            {
                return Results.LocalRedirect(Volver(ex.Message));
            }
        }).RequireAuthorization();
    }

    /// <summary>Solo se vuelve a rutas de esta misma aplicación (evita redirecciones a sitios ajenos).</summary>
    private static bool EsLocal(string? retorno) =>
        !string.IsNullOrEmpty(retorno)
        && retorno.StartsWith('/')
        && !retorno.StartsWith("//", StringComparison.Ordinal)
        && !retorno.StartsWith("/\\", StringComparison.Ordinal);
}
