using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Consulta;

/// <summary>El resumen del Home: cuántos documentos vigentes hay por área, subárea (unidad) y tipo.</summary>
public class ResumenCatalogoTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    /// <summary>Publica un documento en la unidad indicada y lo deja Vigente.</summary>
    private static async Task<DocumentoDetalleDto> PublicarEnAsync(Escenario e, EscenarioFlujo f, int subAreaId, string titulo)
    {
        var doc = await e.Documentos.CrearAsync(f.Autor, new(titulo, f.AreaId, subAreaId, f.TipoId));
        var revisionId = doc.Revisiones.Single().Id;
        await e.SubirPdfAsync(f.Autor, revisionId, titulo);
        await e.Flujo.IniciarAsync(f.Autor, revisionId, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        await e.AprobarRevisionAsync(f, revisionId);
        return doc;
    }

    private static async Task<ResumenAreaDto> AreaAsync(Escenario e, int areaId) =>
        (await e.Consulta().ResumenCatalogoAsync()).Areas.Single(a => a.Id == areaId);

    [Fact]
    public async Task Cuenta_los_documentos_vigentes_por_area_y_por_subarea()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var l2 = await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "L2", "Línea 2");
        var gen = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Id == f.AreaId).SubAreas.Single(s => s.Codigo == "GEN").Id;
        await e.PublicarPrimeraAsync(f);                                  // el documento de PrepararFlujo vive en GEN
        await PublicarEnAsync(e, f, l2, "Uno en L2");
        await PublicarEnAsync(e, f, l2, "Dos en L2");

        var area = await AreaAsync(e, f.AreaId);

        Assert.Equal(3, area.Documentos);
        Assert.Equal(1, area.SubAreas.Single(s => s.Id == gen).Documentos);
        Assert.Equal(2, area.SubAreas.Single(s => s.Id == l2).Documentos);
        Assert.Equal(("GEN", "Unidad general"), (area.SubAreas.Single(s => s.Id == gen).Codigo, area.SubAreas.Single(s => s.Id == gen).Nombre));
        Assert.Equal(f.CodigoArea, area.Codigo);
    }

    [Fact]
    public async Task No_cuenta_borradores_ni_documentos_en_flujo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();                            // borrador
        var borrador = await AreaAsync(e, f.AreaId);

        await e.IniciarAsync(f);                                          // en revisión, aún no vigente
        var enFlujo = await AreaAsync(e, f.AreaId);

        Assert.Equal(0, borrador.Documentos);
        Assert.Equal(0, enFlujo.Documentos);
    }

    [Fact]
    public async Task Una_revision_obsoleta_o_un_documento_retirado_ya_no_cuentan()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var l2 = await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "L2", "Línea 2");
        await e.PublicarPrimeraAsync(f);
        var otro = await PublicarEnAsync(e, f, l2, "Otro");
        Assert.Equal(2, (await AreaAsync(e, f.AreaId)).Documentos);

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, otro.Id, "Ya no aplica");

        var area = await AreaAsync(e, f.AreaId);
        Assert.Equal(1, area.Documentos);
        Assert.Equal(0, area.SubAreas.Single(s => s.Id == l2).Documentos);
    }

    [Fact]
    public async Task Una_nueva_revision_publicada_no_duplica_el_conteo_del_documento()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id);   // Rev.00 queda obsoleta, Rev.01 vigente

        Assert.Equal(1, (await AreaAsync(e, f.AreaId)).Documentos);
    }

    [Fact]
    public async Task Cuenta_los_documentos_por_tipo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        var tipos = (await e.Consulta().ResumenCatalogoAsync()).Tipos;

        Assert.Equal(1, tipos.Single(t => t.Id == f.TipoId).Documentos);
    }

    [Fact]
    public async Task Las_areas_subareas_y_tipos_desactivados_no_aparecen()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var apagada = await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "OFF", "Apagada");
        await e.Catalogo.ActualizarSubAreaAsync(f.Admin, apagada, "Apagada", activa: false);
        var (otraArea, _, otroTipo, _) = await e.NuevaAreaConTipoAsync(f.Admin);
        await e.Catalogo.ActualizarAreaAsync(f.Admin, otraArea, "Cerrada", activa: false);
        await e.Catalogo.ActualizarTipoAsync(f.Admin, otroTipo, "Tipo apagado", 12, activo: false);

        var resumen = await e.Consulta().ResumenCatalogoAsync();

        Assert.DoesNotContain(resumen.Areas.Single(a => a.Id == f.AreaId).SubAreas, s => s.Id == apagada);
        Assert.DoesNotContain(resumen.Areas, a => a.Id == otraArea);
        Assert.DoesNotContain(resumen.Tipos, t => t.Id == otroTipo);
    }

    [Fact]
    public async Task Un_area_sin_documentos_aparece_con_cero()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var area = await AreaAsync(e, f.AreaId);

        Assert.Equal(0, area.Documentos);
        Assert.Equal(0, Assert.Single(area.SubAreas).Documentos);
    }

    [Fact]
    public async Task Un_documento_anterior_al_estandar_sin_unidad_cuenta_en_el_area_pero_en_ninguna_subarea()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        await using (var db = e.Db())
        {
            await db.Documentos.Where(d => d.Id == f.Documento.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.SubAreaId, (int?)null));
        }

        var area = await AreaAsync(e, f.AreaId);

        Assert.Equal(1, area.Documentos);
        Assert.All(area.SubAreas, s => Assert.Equal(0, s.Documentos));
    }

    [Fact]
    public async Task Las_subareas_y_los_tipos_salen_ordenados()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "ZZ", "Zeta");
        await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "AA", "Alfa");

        var resumen = await e.Consulta().ResumenCatalogoAsync();

        var nombres = resumen.Areas.Single(a => a.Id == f.AreaId).SubAreas.Select(s => s.Nombre).ToList();
        Assert.Equal(nombres.Order(StringComparer.CurrentCultureIgnoreCase), nombres);
        Assert.Equal(resumen.Tipos.Select(t => t.Codigo).Order(StringComparer.Ordinal), resumen.Tipos.Select(t => t.Codigo));
    }
}
