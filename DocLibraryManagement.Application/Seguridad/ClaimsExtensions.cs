using System.Security.Claims;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Seguridad;

public static class ClaimsExtensions
{
    public static IReadOnlySet<RolSistema> ObtenerRoles(this ClaimsPrincipal usuario)
    {
        var roles = new HashSet<RolSistema>();
        foreach (var claim in usuario.FindAll(ClaimTypes.Role))
        {
            if (Enum.TryParse<RolSistema>(claim.Value, ignoreCase: true, out var rol))
            {
                roles.Add(rol);
            }
        }

        return roles;
    }

    public static IReadOnlySet<string> ObtenerAreas(this ClaimsPrincipal usuario) =>
        usuario.FindAll(ClaimsDoc.Area)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool PuedeEditarEnArea(this ClaimsPrincipal usuario, string codigoArea) =>
        ResolutorAcceso.PuedeEditarEnArea(usuario.ObtenerRoles(), usuario.ObtenerAreas(), codigoArea);
}
