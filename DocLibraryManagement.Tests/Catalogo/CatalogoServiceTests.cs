using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Catalogo;

public class CatalogoServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Theory]
    [InlineData(RolSistema.Lector)]
    [InlineData(RolSistema.Autor)]
    [InlineData(RolSistema.Auditor)]
    [InlineData(RolSistema.Aprobador)]
    public async Task Solo_el_Administrador_modifica_el_catalogo(RolSistema rol)
    {
        using var e = new Escenario(bd);
        var actor = await e.NuevoActorAsync([rol], "CAL");

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Catalogo.CrearAreaAsync(actor, Escenario.CodigoUnico(), "Área"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Catalogo.CrearTipoAsync(actor, Escenario.CodigoUnico(), "Tipo", 12));
    }

    [Fact]
    public async Task Crear_un_area_normaliza_el_codigo_y_deja_bitacora()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var codigo = Escenario.CodigoUnico("c");

        var id = await e.Catalogo.CrearAreaAsync(admin, codigo.ToLowerInvariant(), "  Calidad  ");

        var areas = await e.Catalogo.ListarAreasAsync();
        var creada = Assert.Single(areas, a => a.Id == id);
        Assert.Equal(codigo, creada.Codigo);
        Assert.Equal("Calidad", creada.Nombre);
        Assert.True(creada.Activa);

        var evento = Assert.Single(await e.AuditoriaDeAsync(codigo));
        Assert.Equal("Area.Crear", evento.Accion);
        Assert.Equal(admin.Identificador, evento.Usuario);
    }

    [Fact]
    public async Task No_se_repite_la_abreviatura_de_area_ni_de_tipo()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var codigo = Escenario.CodigoUnico();
        await e.Catalogo.CrearAreaAsync(admin, codigo, "Primera");
        await e.Catalogo.CrearTipoAsync(admin, codigo, "Primero", 12);

        var ex1 = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearAreaAsync(admin, codigo.ToLowerInvariant(), "Otra"));
        var ex2 = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearTipoAsync(admin, codigo, "Otro", 12));

        Assert.Contains(codigo, ex1.Message);
        Assert.Contains(codigo, ex2.Message);
    }

    [Fact]
    public async Task Desactivar_un_area_la_oculta_del_listado_normal_pero_no_la_borra()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, _, _) = await e.NuevaAreaConTipoAsync(admin);

        await e.Catalogo.ActualizarAreaAsync(admin, areaId, "Renombrada", activa: false);

        Assert.DoesNotContain(await e.Catalogo.ListarAreasAsync(), a => a.Id == areaId);
        var conInactivas = await e.Catalogo.ListarAreasAsync(incluirInactivas: true);
        var area = Assert.Single(conInactivas, a => a.Id == areaId);
        Assert.Equal("Renombrada", area.Nombre);
        Assert.False(area.Activa);
    }

    [Fact]
    public async Task Las_subareas_pertenecen_a_un_area_y_no_se_repiten_en_ella()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, _, _) = await e.NuevaAreaConTipoAsync(admin);

        var subId = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "Línea 1");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearSubAreaAsync(admin, areaId, "Línea 1"));
        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == areaId);
        Assert.Equal(subId, Assert.Single(area.SubAreas).Id);
        Assert.Single(await e.AuditoriaDeAsync(subId.ToString()));
    }

    [Fact]
    public async Task Los_meses_de_revision_tienen_limites()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico(), "Tipo", 0));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico(), "Tipo", 121));
        await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico(), "Tipo", 120);
    }

    [Fact]
    public async Task Un_area_ofrece_solo_sus_tipos_asociados_y_activos()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");
        var t1 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Uno", 12);
        var t2 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Dos", 12);
        var t3 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Tres", 12);
        await e.Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [t1, t2]);

        var disponibles = await e.Catalogo.TiposDisponiblesAsync(areaId, null);
        Assert.Equivalent(new[] { t1, t2 }, disponibles.Select(t => t.Id));
        Assert.DoesNotContain(disponibles, t => t.Id == t3);

        await e.Catalogo.ActualizarTipoAsync(admin, t2, "Dos", 12, activo: false);
        Assert.Equal([t1], (await e.Catalogo.TiposDisponiblesAsync(areaId, null)).Select(t => t.Id));
    }

    [Fact]
    public async Task Una_subarea_hereda_los_tipos_del_area_hasta_que_define_los_suyos()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");
        var t1 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Uno", 12);
        var t2 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Dos", 12);
        await e.Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [t1, t2]);
        var subId = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "Línea");

        Assert.Equivalent(new[] { t1, t2 }, (await e.Catalogo.TiposDisponiblesAsync(areaId, subId)).Select(t => t.Id));

        await e.Catalogo.EstablecerTiposDeSubAreaAsync(admin, subId, [t2]);
        Assert.Equal([t2], (await e.Catalogo.TiposDisponiblesAsync(areaId, subId)).Select(t => t.Id));

        // Vaciar los propios devuelve la herencia.
        await e.Catalogo.EstablecerTiposDeSubAreaAsync(admin, subId, []);
        Assert.Equivalent(new[] { t1, t2 }, (await e.Catalogo.TiposDisponiblesAsync(areaId, subId)).Select(t => t.Id));
    }

    [Fact]
    public async Task Reemplazar_las_asociaciones_quita_las_que_ya_no_van_y_deja_bitacora()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var codigoArea = Escenario.CodigoUnico("A");
        var areaId = await e.Catalogo.CrearAreaAsync(admin, codigoArea, "Área");
        var t1 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Uno", 12);
        var t2 = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Dos", 12);

        await e.Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [t1]);
        await e.Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [t2, t2]); // duplicado se ignora

        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == areaId);
        Assert.Equal([t2], area.TipoIds);
        var eventos = (await e.AuditoriaDeAsync(codigoArea)).Where(x => x.Accion == "Area.TiposAsociados").ToList();
        Assert.Equal(2, eventos.Count);
    }

    [Fact]
    public async Task Asociar_un_tipo_inexistente_se_rechaza()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [int.MaxValue]));
    }
}
