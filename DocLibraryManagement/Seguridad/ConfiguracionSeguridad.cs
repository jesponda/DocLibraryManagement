using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;

namespace DocLibraryManagement.Seguridad;

public static class ConfiguracionSeguridad
{
    public const string ModoWindows = "Windows";
    public const string ModoDesarrollo = "Desarrollo";

    public static string ObtenerModo(IConfiguration configuracion) =>
        configuracion["Autenticacion:Modo"] ?? ModoWindows;

    public static IServiceCollection AddSeguridadDocumental(
        this IServiceCollection services,
        IConfiguration configuracion,
        IHostEnvironment entorno)
    {
        services.Configure<AutorizacionOptions>(configuracion.GetSection(AutorizacionOptions.Seccion));
        services.Configure<DesarrolloOptions>(configuracion.GetSection(DesarrolloOptions.Seccion));
        services.AddMemoryCache();
        services.AddHttpContextAccessor();
        services.AddScoped<ProveedorActor>();

        var modo = ObtenerModo(configuracion);
        switch (modo)
        {
            case ModoWindows:
                services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
                break;

            case ModoDesarrollo:
                if (!entorno.IsDevelopment())
                {
                    throw new InvalidOperationException(
                        "El modo de autenticación 'Desarrollo' solo puede usarse en el entorno Development.");
                }

                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(o =>
                    {
                        o.LoginPath = "/dev/login";
                        o.AccessDeniedPath = "/acceso-denegado";
                    });
                break;

            default:
                throw new InvalidOperationException(
                    $"Modo de autenticación '{modo}' no válido. Use '{ModoWindows}' o '{ModoDesarrollo}'.");
        }

        services.AddScoped<IClaimsTransformation, TransformacionClaimsDocumental>();
        services.AddSingleton<IAuthorizationHandler, EdicionEnAreaHandler>();

        services.AddAuthorizationBuilder()
            // Toda página exige usuario autenticado, salvo las marcadas con [AllowAnonymous].
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(Politicas.PuedeEditarDocumentos, p => p.RequireRole(Rol(RolSistema.Autor), Rol(RolSistema.Administrador)))
            .AddPolicy(Politicas.PuedeRevisar, p => p.RequireRole(Rol(RolSistema.Revisor), Rol(RolSistema.Administrador)))
            .AddPolicy(Politicas.PuedeAprobar, p => p.RequireRole(Rol(RolSistema.Aprobador), Rol(RolSistema.Administrador)))
            .AddPolicy(Politicas.PuedeAuditar, p => p.RequireRole(Rol(RolSistema.Auditor), Rol(RolSistema.Administrador)))
            .AddPolicy(Politicas.PuedeAdministrar, p => p.RequireRole(Rol(RolSistema.Administrador)));

        return services;
    }

    private static string Rol(RolSistema rol) => rol.ToString();
}
