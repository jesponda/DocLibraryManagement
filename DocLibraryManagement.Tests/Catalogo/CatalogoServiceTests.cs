using DocLibraryManagement.Application.Catalogo;
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
    public async Task Las_subareas_pertenecen_a_un_area_y_ni_el_nombre_ni_la_abreviatura_se_repiten_en_ella()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");

        var subId = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "l1", "Línea 1");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearSubAreaAsync(admin, areaId, "L9", "Línea 1"));   // mismo nombre
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearSubAreaAsync(admin, areaId, "L1", "Otra línea")); // misma abreviatura
        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == areaId);
        var sub = Assert.Single(area.SubAreas);
        Assert.Equal(subId, sub.Id);
        Assert.Equal("L1", sub.Codigo);                                       // se normaliza a mayúsculas
        Assert.Single(await e.AuditoriaDeAsync(subId.ToString()));
    }

    [Fact]
    public async Task La_misma_abreviatura_de_subarea_si_puede_existir_en_otra_area()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var area1 = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área 1");
        var area2 = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área 2");

        await e.Catalogo.CrearSubAreaAsync(admin, area1, "SIP", "Shipping");
        await e.Catalogo.CrearSubAreaAsync(admin, area2, "SIP", "Shipments"); // QA-SIP y MT-SIP del estándar de Calidad
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]                 // una sola letra no basta para una unidad
    [InlineData("TIENE ESPACIO")]
    [InlineData("MUYLARGOCODIGO")]
    [InlineData("A-B")]
    public async Task La_abreviatura_de_la_subarea_se_valida(string codigo)
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.CrearSubAreaAsync(admin, areaId, codigo, "Subárea"));
    }

    [Fact]
    public async Task Solo_el_administrador_crea_subareas()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");
        var autor = await e.NuevoAutorAsync();

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Catalogo.CrearSubAreaAsync(autor, areaId, "XX", "Subárea"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Catalogo.AsignarCodigoSubAreaAsync(autor, 1, "XX"));
    }

    [Fact]
    public async Task Una_subarea_anterior_al_estandar_recibe_su_abreviatura_una_sola_vez()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");
        int subId;
        await using (var db = e.Db())
        {
            var sub = new SubArea { AreaId = areaId, Nombre = "Anterior" }; // como las que ya existían sin abreviatura
            db.SubAreas.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        await e.Catalogo.AsignarCodigoSubAreaAsync(admin, subId, "ant");

        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == areaId);
        Assert.Equal("ANT", Assert.Single(area.SubAreas).Codigo);
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.AsignarCodigoSubAreaAsync(admin, subId, "OTRA")); // ya no cambia
        Assert.Contains(await e.AuditoriaDeAsync(subId.ToString()), a => a.Accion == "SubArea.AsignarCodigo");
    }

    [Fact]
    public async Task No_se_puede_asignar_una_abreviatura_que_ya_usa_otra_subarea_del_area()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var areaId = await e.Catalogo.CrearAreaAsync(admin, Escenario.CodigoUnico("A"), "Área");
        await e.Catalogo.CrearSubAreaAsync(admin, areaId, "USO", "En uso");
        int subId;
        await using (var db = e.Db())
        {
            var sub = new SubArea { AreaId = areaId, Nombre = "Anterior" };
            db.SubAreas.Add(sub);
            await db.SaveChangesAsync();
            subId = sub.Id;
        }

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.AsignarCodigoSubAreaAsync(admin, subId, "USO"));
    }

    [Theory]
    [InlineData("P")]
    [InlineData("F")]
    [InlineData("R")]
    [InlineData("DESV")]
    public async Task Los_tipos_pueden_tener_abreviatura_de_una_letra_como_en_el_estandar_de_calidad(string codigo)
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var codigoUnico = codigo + Escenario.CodigoUnico("")[..3]; // evita chocar con tipos de otras pruebas

        // Las pruebas comparten base: se usa un código distinto, pero se comprueba la regla sobre la forma.
        Assert.Equal(codigo, ReglasCatalogo.NormalizarCodigo(codigo, "el tipo", ReglasCatalogo.LongitudMinimaCodigoTipo));
        await e.Catalogo.CrearTipoAsync(admin, codigoUnico, "Tipo", 12);
    }

    [Fact]
    public void Las_areas_y_las_subareas_siguen_pidiendo_al_menos_dos_caracteres()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo("P", "el área"));
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo("", "el tipo", ReglasCatalogo.LongitudMinimaCodigoTipo));
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
        var subId = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "LN", "Línea");

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
