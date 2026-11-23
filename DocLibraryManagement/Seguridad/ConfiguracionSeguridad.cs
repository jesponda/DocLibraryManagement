using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace DocLibraryManagement.Seguridad;

public static class ConfiguracionSeguridad
{
    public const string ModoWindows = "Windows";
    public const string ModoDesarrollo = "Desarrollo";

    /// <summary>Lista de usuarios propia de la aplicación, con usuario y contraseña (sin Active Directory).</summary>
    public const string ModoLocal = "Local";

    public static string ObtenerModo(IConfiguration configuracion) =>
        configuracion["Autenticacion:Modo"] ?? ModoLocal;

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

            case ModoLocal:
                services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                    .AddCookie(o =>
                    {
                        o.LoginPath = "/acceso";
                        o.AccessDeniedPath = "/acceso-denegado";
                        o.Cookie.Name = "SGD.Sesion";
                        o.Cookie.HttpOnly = true;
                        o.Cookie.SameSite = SameSiteMode.Lax;
                        // Detrás de HTTPS la cookie viaja solo cifrada; si el sitio se sirviera por HTTP interno, sigue funcionando.
                        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                        o.ExpireTimeSpan = TimeSpan.FromHours(8);
                        o.SlidingExpiration = true;
                        o.Events.OnValidatePrincipal = SesionLocal.ValidarAsync;
                    });
                // Las claves cifran la cookie de sesión y los mensajes firmados. Con una carpeta fija sobreviven a reinicios y
                // a cambios de cuenta de servicio; sin ella se usa el valor por omisión de ASP.NET (perfil del usuario).
                var dp = services.AddDataProtection().SetApplicationName("DocLibraryManagement");
                if (configuracion["DataProtection:RutaClaves"] is { Length: > 0 } rutaClaves)
                {
                    dp.PersistKeysToFileSystem(new DirectoryInfo(rutaClaves));
                }


                break;

            default:
                throw new InvalidOperationException(
                    $"Modo de autenticación '{modo}' no válido. Use '{ModoLocal}', '{ModoWindows}' o '{ModoDesarrollo}'.");
        }

        // En modo Local los roles y áreas salen de la lista de usuarios de la aplicación (no hay grupos de AD que resolver).
        if (modo != ModoLocal)
        {
            services.AddScoped<IClaimsTransformation, TransformacionClaimsDocumental>();
        }

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
