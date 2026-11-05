using System.Security.Claims;
using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Usuarios simulados SOLO para desarrollo. Permiten probar roles y áreas sin Active Directory.
/// El arranque falla si el modo "Desarrollo" se activa fuera del entorno Development.
/// </summary>
public class DesarrolloOptions
{
    public const string Seccion = "Desarrollo";

    public List<PersonaDesarrollo> Personas { get; set; } = [];
}

public class PersonaDesarrollo
{
    public string Id { get; set; } = "";

    public string Nombre { get; set; } = "";

    public List<RolSistema> Roles { get; set; } = [];

    public List<string> Areas { get; set; } = [];
}

public static class EndpointsDesarrollo
{
    public static void MapEndpointsDesarrollo(this WebApplication app)
    {
        app.MapPost("/dev/login/{id}", async (string id, HttpContext http, IOptions<DesarrolloOptions> opciones) =>
        {
            var persona = opciones.Value.Personas.FirstOrDefault(p => p.Id == id);
            if (persona is null)
            {
                // Un 404 re-ejecutaría la página "no encontrado", que exige antiforgery en un POST.
                return Results.LocalRedirect("/dev/login");
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, $"DEV\\{persona.Id}"),
                new(ClaimTypes.NameIdentifier, persona.Id),
            };
            claims.AddRange(persona.Roles.Append(RolSistema.Lector).Distinct()
                .Select(r => new Claim(ClaimTypes.Role, r.ToString())));
            claims.AddRange(persona.Areas.Select(a => new Claim(ClaimsDoc.Area, a)));

            var identidad = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identidad));
            return Results.LocalRedirect("/");
        }).AllowAnonymous();

        app.MapPost("/dev/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/dev/login");
        }).AllowAnonymous();
    }
}
