using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Principal;
using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Convierte la pertenencia a grupos de AD en roles y áreas de la aplicación, y sincroniza
/// al usuario en el primer acceso. El resultado se cachea por usuario (MinutosCache), por lo que
/// un cambio de grupos en AD tarda como máximo ese tiempo en reflejarse.
/// </summary>
internal sealed class TransformacionClaimsDocumental(
    IOptions<AutorizacionOptions> opciones,
    IMemoryCache cache,
    IUsuarioSincronizador sincronizador,
    ILogger<TransformacionClaimsDocumental> log) : IClaimsTransformation
{
    public const string TipoAutenticacion = "DocLibrary";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true } identidad
            || string.IsNullOrWhiteSpace(identidad.Name))
        {
            return principal;
        }

        // La transformación puede invocarse más de una vez por solicitud.
        if (principal.Identities.Any(i => i.AuthenticationType == TipoAutenticacion))
        {
            return principal;
        }

        var nombre = identidad.Name;
        var clave = $"acceso:{nombre}";

        if (!cache.TryGetValue(clave, out AccesoUsuario? acceso) || acceso is null)
        {
            acceso = CalcularAcceso(principal, opciones.Value);

            try
            {
                await sincronizador.SincronizarAsync(nombre, nombre, correo: null);
                cache.Set(clave, acceso, TimeSpan.FromMinutes(Math.Max(1, opciones.Value.MinutosCache)));
            }
            catch (Exception ex)
            {
                // No se cachea: se reintenta en la siguiente solicitud.
                log.LogError(ex, "No se pudo sincronizar al usuario {Usuario}", nombre);
            }
        }

        // En modo desarrollo los roles y áreas ya vienen en la cookie.
        if (!EsIdentidadWindows(identidad))
        {
            return principal;
        }

        var clon = principal.Clone();
        var identidadApp = new ClaimsIdentity(TipoAutenticacion, ClaimTypes.Name, ClaimTypes.Role);
        foreach (var rol in acceso.Roles)
        {
            identidadApp.AddClaim(new Claim(ClaimTypes.Role, rol.ToString()));
        }

        foreach (var area in acceso.Areas)
        {
            identidadApp.AddClaim(new Claim(ClaimsDoc.Area, area));
        }

        clon.AddIdentity(identidadApp);
        return clon;
    }

    private static bool EsIdentidadWindows(IIdentity identidad) => identidad is WindowsIdentity;

    private static AccesoUsuario CalcularAcceso(ClaimsPrincipal principal, AutorizacionOptions opciones)
    {
        if (OperatingSystem.IsWindows() && principal.Identity is WindowsIdentity windows)
        {
            return CalcularAccesoWindows(windows, opciones);
        }

        return new AccesoUsuario(principal.ObtenerRoles(), principal.ObtenerAreas());
    }

    [SupportedOSPlatform("windows")]
    private static AccesoUsuario CalcularAccesoWindows(WindowsIdentity identidad, AutorizacionOptions opciones)
    {
        var wp = new WindowsPrincipal(identidad);
        return ResolutorAcceso.Resolver(opciones.Grupos, grupo => wp.IsInRole(grupo));
    }
}
