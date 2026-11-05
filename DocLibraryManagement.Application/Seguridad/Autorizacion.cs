using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Seguridad;

/// <summary>Tipos de claim propios de la aplicación.</summary>
public static class ClaimsDoc
{
    /// <summary>Código de un área en la que el usuario puede editar (se repite una vez por área).</summary>
    public const string Area = "doclibrary/area";
}

/// <summary>Nombres de las políticas de autorización.</summary>
public static class Politicas
{
    public const string PuedeEditarDocumentos = nameof(PuedeEditarDocumentos);
    public const string PuedeRevisar = nameof(PuedeRevisar);
    public const string PuedeAprobar = nameof(PuedeAprobar);
    public const string PuedeAuditar = nameof(PuedeAuditar);
    public const string PuedeAdministrar = nameof(PuedeAdministrar);
}

/// <summary>Opciones enlazadas desde la sección "Autorizacion" de appsettings.</summary>
public class AutorizacionOptions
{
    public const string Seccion = "Autorizacion";

    public List<MapeoGrupoRol> Grupos { get; set; } = [];

    /// <summary>Minutos que se cachea la resolución de grupos de un usuario.</summary>
    public int MinutosCache { get; set; } = 10;
}

/// <summary>
/// Asocia un grupo de AD con un rol. Para el rol Autor, <see cref="Areas"/> indica
/// los códigos de área que el grupo habilita para edición.
/// </summary>
public class MapeoGrupoRol
{
    public string Grupo { get; set; } = "";

    public RolSistema Rol { get; set; }

    public List<string> Areas { get; set; } = [];
}

public sealed record AccesoUsuario(IReadOnlySet<RolSistema> Roles, IReadOnlySet<string> Areas)
{
    public static AccesoUsuario SinAcceso { get; } =
        new(new HashSet<RolSistema>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}

public static class ResolutorAcceso
{
    /// <summary>
    /// Calcula roles y áreas del usuario a partir de su pertenencia a grupos.
    /// Todo empleado autenticado es al menos Lector (lectura abierta a lo vigente).
    /// </summary>
    public static AccesoUsuario Resolver(
        IEnumerable<MapeoGrupoRol> mapeos,
        Func<string, bool> perteneceAGrupo)
    {
        var roles = new HashSet<RolSistema> { RolSistema.Lector };
        var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mapeo in mapeos)
        {
            if (string.IsNullOrWhiteSpace(mapeo.Grupo) || !perteneceAGrupo(mapeo.Grupo))
            {
                continue;
            }

            roles.Add(mapeo.Rol);
            foreach (var area in mapeo.Areas.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                areas.Add(area.Trim());
            }
        }

        return new AccesoUsuario(roles, areas);
    }

    /// <summary>
    /// Un Administrador edita en cualquier área. Un Autor, solo en las áreas habilitadas.
    /// </summary>
    public static bool PuedeEditarEnArea(IReadOnlySet<RolSistema> roles, IReadOnlySet<string> areas, string codigoArea) =>
        roles.Contains(RolSistema.Administrador)
        || (roles.Contains(RolSistema.Autor) && areas.Contains(codigoArea));
}
