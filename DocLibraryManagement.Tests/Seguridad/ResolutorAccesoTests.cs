using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Tests.Seguridad;

public class ResolutorAccesoTests
{
    private static readonly List<MapeoGrupoRol> Mapeos =
    [
        new() { Grupo = "Admins", Rol = RolSistema.Administrador },
        new() { Grupo = "AutoresCal", Rol = RolSistema.Autor, Areas = ["CAL"] },
        new() { Grupo = "AutoresMan", Rol = RolSistema.Autor, Areas = ["MAN", "SEG"] },
        new() { Grupo = "Aprobadores", Rol = RolSistema.Aprobador },
    ];

    private static AccesoUsuario Resolver(params string[] grupos) =>
        ResolutorAcceso.Resolver(Mapeos, g => grupos.Contains(g));

    [Fact]
    public void Todo_usuario_autenticado_es_al_menos_Lector()
    {
        var acceso = Resolver();

        Assert.Equal([RolSistema.Lector], acceso.Roles);
        Assert.Empty(acceso.Areas);
    }

    [Fact]
    public void Un_grupo_otorga_su_rol_y_sus_areas()
    {
        var acceso = Resolver("AutoresCal");

        Assert.Contains(RolSistema.Autor, acceso.Roles);
        Assert.Contains(RolSistema.Lector, acceso.Roles);
        Assert.Equal(["CAL"], acceso.Areas);
    }

    [Fact]
    public void Varios_grupos_acumulan_roles_y_areas()
    {
        var acceso = Resolver("AutoresCal", "AutoresMan", "Aprobadores");

        Assert.Contains(RolSistema.Autor, acceso.Roles);
        Assert.Contains(RolSistema.Aprobador, acceso.Roles);
        Assert.Equivalent(new[] { "CAL", "MAN", "SEG" }, acceso.Areas);
    }

    [Fact]
    public void Un_grupo_inexistente_o_vacio_no_otorga_nada()
    {
        var mapeos = new List<MapeoGrupoRol> { new() { Grupo = "", Rol = RolSistema.Administrador } };

        var acceso = ResolutorAcceso.Resolver(mapeos, _ => true);

        Assert.Equal([RolSistema.Lector], acceso.Roles);
    }

    [Fact]
    public void Las_areas_no_distinguen_mayusculas()
    {
        var acceso = Resolver("AutoresCal");

        Assert.Contains("cal", acceso.Areas);
    }

    [Theory]
    [InlineData("CAL", true)]
    [InlineData("cal", true)]
    [InlineData("MAN", false)]
    public void Un_Autor_solo_edita_en_sus_areas(string area, bool esperado)
    {
        var acceso = Resolver("AutoresCal");

        Assert.Equal(esperado, ResolutorAcceso.PuedeEditarEnArea(acceso.Roles, acceso.Areas, area));
    }

    [Fact]
    public void Un_Administrador_edita_en_cualquier_area()
    {
        var acceso = Resolver("Admins");

        Assert.True(ResolutorAcceso.PuedeEditarEnArea(acceso.Roles, acceso.Areas, "XYZ"));
    }

    [Fact]
    public void Un_Aprobador_sin_rol_Autor_no_edita()
    {
        var acceso = Resolver("Aprobadores");

        Assert.False(ResolutorAcceso.PuedeEditarEnArea(acceso.Roles, acceso.Areas, "CAL"));
    }

    [Fact]
    public void Un_Autor_sin_areas_no_edita_en_ninguna()
    {
        var roles = new HashSet<RolSistema> { RolSistema.Lector, RolSistema.Autor };

        Assert.False(ResolutorAcceso.PuedeEditarEnArea(roles, new HashSet<string>(), "CAL"));
    }
}
