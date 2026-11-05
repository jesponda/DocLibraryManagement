using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Tests.Catalogo;

public class ReglasCatalogoTests
{
    [Theory]
    [InlineData("cal", "CAL")]
    [InlineData("  pro  ", "PRO")]
    [InlineData("Ab12", "AB12")]
    [InlineData("ABCDEFGHIJ", "ABCDEFGHIJ")]
    public void El_codigo_se_normaliza_a_mayusculas_sin_espacios(string entrada, string esperado)
    {
        Assert.Equal(esperado, ReglasCatalogo.NormalizarCodigo(entrada, "el área"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("ABCDEFGHIJK")]
    [InlineData("CA L")]
    [InlineData("CAL-")]
    [InlineData("CÁL")]
    [InlineData("C@L")]
    public void Un_codigo_invalido_se_rechaza(string? entrada)
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo(entrada, "el área"));
    }

    [Fact]
    public void Un_codigo_con_guion_se_rechaza_porque_rompe_el_formato_TTT_AAA_NNN()
    {
        // El guion es el separador del código del documento: no puede aparecer dentro de una abreviatura.
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo("CA-L", "el área"));
    }

    [Fact]
    public void El_nombre_es_obligatorio_y_tiene_limite()
    {
        Assert.Equal("Calidad", ReglasCatalogo.NormalizarNombre("  Calidad ", "el área"));
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarNombre("  ", "el área"));
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarNombre(new string('x', 201), "el área"));
    }

    [Fact]
    public void Una_subarea_sin_tipos_propios_hereda_los_del_area()
    {
        var efectivos = ReglasCatalogo.TiposEfectivos([1, 2, 3], []);

        Assert.Equal([1, 2, 3], efectivos);
    }

    [Fact]
    public void Una_subarea_con_tipos_propios_usa_solo_esos()
    {
        var efectivos = ReglasCatalogo.TiposEfectivos([1, 2, 3], [2]);

        Assert.Equal([2], efectivos);
    }

    [Fact]
    public void Sin_subarea_se_usan_los_del_area()
    {
        Assert.Equal([1, 2], ReglasCatalogo.TiposEfectivos([1, 2], null));
    }

    [Fact]
    public void El_Actor_decide_la_edicion_por_area_con_las_mismas_reglas_que_la_autorizacion()
    {
        var autor = new Actor("X", new HashSet<RolSistema> { RolSistema.Autor }, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CAL" });
        var admin = new Actor("A", new HashSet<RolSistema> { RolSistema.Administrador }, new HashSet<string>());

        Assert.True(autor.PuedeEditarEnArea("cal"));
        Assert.False(autor.PuedeEditarEnArea("MAN"));
        Assert.True(admin.PuedeEditarEnArea("CUALQUIERA"));
    }
}
