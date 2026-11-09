using System.Security.Claims;
using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Comun;

/// <summary>
/// Quién ejecuta una operación. Los servicios reciben esto (no el HttpContext) para poder
/// validar permisos por sí mismos y para poder probarse sin web.
/// </summary>
public sealed record Actor(
    string Identificador,
    IReadOnlySet<RolSistema> Roles,
    IReadOnlySet<string> Areas,
    string? DireccionIp = null)
{
    /// <summary>Identificador con el que la bitácora registra lo que hace el sistema por sí mismo (tareas programadas).</summary>
    public const string IdentificadorSistema = "SISTEMA";

    /// <summary>Actor para tareas automáticas. No tiene roles: no puede usarse para operaciones de usuario.</summary>
    public static Actor Sistema { get; } = new(IdentificadorSistema, new HashSet<RolSistema>(), new HashSet<string>());

    public bool EsAdministrador => Roles.Contains(RolSistema.Administrador);

    public bool TieneRol(RolSistema rol) => Roles.Contains(rol);

    public bool PuedeEditarEnArea(string codigoArea) =>
        ResolutorAcceso.PuedeEditarEnArea(Roles, Areas, codigoArea);

    public static Actor Desde(ClaimsPrincipal usuario, string? direccionIp = null) =>
        new(usuario.Identity?.Name ?? throw new InvalidOperationException("Usuario no autenticado."),
            usuario.ObtenerRoles(),
            usuario.ObtenerAreas(),
            direccionIp);
}

/// <summary>Una regla de negocio no se cumple. El mensaje está en español y es apto para mostrarse al usuario.</summary>
public class ReglaNegocioException(string mensaje) : Exception(mensaje);

/// <summary>El actor no tiene permiso para la operación.</summary>
public class AccesoDenegadoException(string mensaje = "No tienes permiso para realizar esta acción.")
    : Exception(mensaje);
