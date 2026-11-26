using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Catalogo;

/// <summary>Los datos del catálogo estándar de Calidad (sin base de datos).</summary>
public class CatalogoEstandarDatosTests
{
    [Fact]
    public void Los_datos_cumplen_las_reglas_del_catalogo()
    {
        Assert.Empty(CatalogoEstandar.Validar());
    }

    [Fact]
    public void Trae_los_20_tipos_documentales_de_la_nomenclatura_de_calidad()
    {
        var esperados = new[] { "P", "F", "DP", "CP", "AE", "JSA", "PFEP", "LOTO", "PO", "MC", "AV", "HTE", "AC", "PI", "CHL", "DF", "PFM", "PC", "R", "DESV" };

        Assert.Equal(esperados.Order(), CatalogoEstandar.Tipos.Select(t => t.Codigo).Order());
        Assert.Equal("Procedimiento", CatalogoEstandar.Tipos.Single(t => t.Codigo == "P").Nombre);
        Assert.Equal("Hoja de Trabajo Estándar", CatalogoEstandar.Tipos.Single(t => t.Codigo == "HTE").Nombre);
    }

    [Fact]
    public void Trae_las_9_areas_con_sus_unidades()
    {
        var porArea = CatalogoEstandar.Areas.ToDictionary(a => a.Codigo, a => a.Unidades.Count);

        Assert.Equal(
            new Dictionary<string, int> { ["QA"] = 15, ["PR"] = 12, ["ENG"] = 4, ["MT"] = 7, ["IT"] = 7, ["FN"] = 7, ["HR"] = 7, ["EHS"] = 5, ["SGD"] = 11 },
            porArea);
    }

    [Fact]
    public void Cada_area_trae_su_unidad_General_GEN_primero_para_los_documentos_de_toda_el_area()
    {
        foreach (var area in CatalogoEstandar.Areas)
        {
            var primera = area.Unidades[0];
            Assert.Equal((ReglasCatalogo.UnidadGeneral, ReglasCatalogo.NombreUnidadGeneral), (primera.Codigo, primera.Nombre));
            Assert.Single(area.Unidades, u => u.Codigo == ReglasCatalogo.UnidadGeneral);
        }
    }

    [Fact]
    public void Las_funciones_se_aplanan_como_subareas_con_el_nombre_subarea_funcion()
    {
        var qa = CatalogoEstandar.Areas.Single(a => a.Codigo == "QA");
        var it = CatalogoEstandar.Areas.Single(a => a.Codigo == "IT");

        Assert.Equal("Process Quality · Die Cast", qa.Unidades.Single(u => u.Codigo == "DC").Nombre);
        Assert.Equal("Incoming Inspection", qa.Unidades.Single(u => u.Codigo == "II").Nombre);   // sin funciones: es en sí la unidad
        Assert.Equal("Information Technology · Applications", it.Unidades.Single(u => u.Codigo == "AP").Nombre);
        Assert.Equal("ITI · Telecomunicaciones", it.Unidades.Single(u => u.Codigo == "TE").Nombre);
    }

    [Fact]
    public void Una_misma_abreviatura_puede_existir_en_areas_distintas_pero_no_dos_veces_en_una()
    {
        var sip = CatalogoEstandar.Areas.Where(a => a.Unidades.Any(u => u.Codigo == "SIP")).Select(a => a.Codigo).ToList();
        var dc = CatalogoEstandar.Areas.Where(a => a.Unidades.Any(u => u.Codigo == "DC")).Select(a => a.Codigo).ToList();

        Assert.Equal(["QA", "MT"], sip);      // Shipping y Shipments
        Assert.Equal(["QA", "PR"], dc);       // Process Quality · Die Cast y Die Cast de Production
        Assert.All(CatalogoEstandar.Areas, a => Assert.Equal(a.Unidades.Count, a.Unidades.Select(u => u.Codigo).Distinct().Count()));
    }

    [Fact]
    public void La_revision_es_anual_y_el_plan_de_inspeccion_no_aplica_en_metrologia()
    {
        Assert.Equal(12, CatalogoEstandar.MesesRevision);
        Assert.Equal(["PI"], CatalogoEstandar.TiposNoAplicables[("QA", "MET")]);
        Assert.Single(CatalogoEstandar.TiposNoAplicables);
    }

    [Fact]
    public void El_orden_del_home_solo_menciona_areas_que_existen_y_la_transversal_va_aparte()
    {
        var codigos = CatalogoEstandar.Areas.Select(a => a.Codigo).ToHashSet();

        Assert.All(CatalogoEstandar.OrdenHome, c => Assert.Contains(c, codigos));
        Assert.Contains(CatalogoEstandar.AreaTransversal, codigos);
        Assert.DoesNotContain(CatalogoEstandar.AreaTransversal, CatalogoEstandar.OrdenHome);
    }

    [Fact]
    public void El_flujo_por_omision_es_revision_y_luego_aprobacion()
    {
        Assert.Equal([("Revisión", 1, false), ("Aprobación", 2, true)], CatalogoEstandar.FlujoPorOmision);
    }

    [Fact]
    public void Un_dato_invalido_se_detecta()
    {
        // La validación es lo que protege al cargador: se comprueba que sí detecta errores (con las mismas reglas del catálogo).
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo("X", "unidad"));        // una letra no basta para una unidad
        Assert.Throws<ReglaNegocioException>(() => ReglasCatalogo.NormalizarCodigo("TIENE ESPACIO", "tipo", 1));
        Assert.Equal("P", ReglasCatalogo.NormalizarCodigo(" p ", "tipo", 1));
    }
}

/// <summary>La carga del catálogo estándar contra una base real. Cada prueba usa una base limpia: el cargador cuenta lo que crea.</summary>
public class CatalogoEstandarCargaTests
{
    private static async Task<(BaseDatosFixture Bd, Escenario E, Actor Admin)> LimpioAsync(IReadOnlyDictionary<string, string?>? extra = null)
    {
        var bd = new BaseDatosFixture();
        await bd.InitializeAsync();
        var e = new Escenario(bd, extra: extra);
        return (bd, e, await e.NuevoAdminAsync());
    }

    private static ICatalogoEstandarService Cargador(Escenario e) => e.Scope().GetRequiredService<ICatalogoEstandarService>();

    private static async Task LiberarAsync(BaseDatosFixture bd, Escenario e)
    {
        e.Dispose();
        await bd.DisposeAsync();
    }

    // ---------- Carga ----------

    [Fact]
    public async Task La_primera_carga_crea_todo_el_catalogo()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            var r = await Cargador(e).CargarAsync(admin);

            var unidades = CatalogoEstandar.Areas.Sum(a => a.Unidades.Count);
            var asociaciones = CatalogoEstandar.Areas.Count * CatalogoEstandar.Tipos.Count
                               + CatalogoEstandar.Tipos.Count - CatalogoEstandar.TiposNoAplicables[("QA", "MET")].Count; // las unidades propias de Metrología
            Assert.Equal(new ResultadoCargaCatalogo(9, unidades, 20, asociaciones, 20), r);
            Assert.Equal(75, unidades);   // 66 del estándar + la unidad «General» (GEN) de cada una de las 9 áreas
            Assert.False(r.SinCambios);

            var areas = await e.Catalogo.ListarAreasAsync();
            Assert.Equal(9, areas.Count);
            Assert.Equal(20, (await e.Catalogo.ListarTiposAsync()).Count);
            Assert.All(areas, a => Assert.All(a.SubAreas, s => Assert.False(string.IsNullOrEmpty(s.Codigo))));
            Assert.All(await e.Catalogo.ListarTiposAsync(), t => Assert.Equal(12, t.MesesRevision));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Cargar_de_nuevo_no_cambia_nada()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            await using var antes = e.Db();
            var conteos = (await antes.Areas.CountAsync(), await antes.SubAreas.CountAsync(), await antes.TiposDocumento.CountAsync(),
                           await antes.AreasTiposDocumento.CountAsync(), await antes.SubAreasTiposDocumento.CountAsync(), await antes.PasosPlantilla.CountAsync());

            var r = await Cargador(e).CargarAsync(admin);

            Assert.True(r.SinCambios);
            await using var despues = e.Db();
            Assert.Equal(conteos, (await despues.Areas.CountAsync(), await despues.SubAreas.CountAsync(), await despues.TiposDocumento.CountAsync(),
                                   await despues.AreasTiposDocumento.CountAsync(), await despues.SubAreasTiposDocumento.CountAsync(), await despues.PasosPlantilla.CountAsync()));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Deja_la_carga_en_la_bitacora_y_la_segunda_dice_que_ya_estaba_cargado()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            await Cargador(e).CargarAsync(admin);

            var eventos = (await e.AuditoriaDeAsync("estandar")).Where(a => a.Accion == "Catalogo.CargarEstandar").OrderBy(a => a.Id).ToList();
            Assert.Equal(2, eventos.Count);
            Assert.Equal(admin.Identificador, eventos[0].Usuario);
            Assert.Contains("Areas=9", eventos[0].ValorNuevo);
            Assert.Contains("Tipos=20", eventos[0].ValorNuevo);
            Assert.Contains("ya estaba cargado", eventos[1].Comentario);
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Solo_el_administrador_puede_cargarlo_y_si_no_se_crea_nada()
    {
        var (bd, e, _) = await LimpioAsync();
        try
        {
            foreach (var actor in new[] { await e.NuevoAutorAsync("QA"), await e.NuevoActorAsync([RolSistema.Auditor]), await e.NuevoAprobadorAsync() })
            {
                await Assert.ThrowsAsync<AccesoDenegadoException>(() => Cargador(e).CargarAsync(actor));
            }

            Assert.Empty(await e.Catalogo.ListarAreasAsync(incluirInactivas: true));
            Assert.Empty(await e.Catalogo.ListarTiposAsync(incluirInactivos: true));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    // ---------- No pisa lo que ya existe ----------

    [Fact]
    public async Task No_cambia_nombres_activacion_ni_meses_de_lo_que_ya_existe()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            var areaQa = await e.Catalogo.CrearAreaAsync(admin, "QA", "Calidad de planta");
            await e.Catalogo.ActualizarAreaAsync(admin, areaQa, "Calidad de planta", activa: false);
            var tipoP = await e.Catalogo.CrearTipoAsync(admin, "P", "Mi procedimiento", 24);

            var r = await Cargador(e).CargarAsync(admin);

            var qa = (await e.Catalogo.ListarAreasAsync(incluirInactivas: true)).Single(a => a.Codigo == "QA");
            Assert.Equal((areaQa, "Calidad de planta", false), (qa.Id, qa.Nombre, qa.Activa));   // sigue como la dejó el Administrador
            Assert.Equal(15, qa.SubAreas.Count);                                                     // pero se le completaron las unidades
            var p = (await e.Catalogo.ListarTiposAsync()).Single(t => t.Codigo == "P");
            Assert.Equal((tipoP, "Mi procedimiento", 24), (p.Id, p.Nombre, p.MesesRevision));
            Assert.Equal(8, r.AreasNuevas);                                                          // QA ya existía
            Assert.Equal(19, r.TiposNuevos);                                                         // P ya existía
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Una_subarea_anterior_con_el_mismo_nombre_pero_sin_abreviatura_se_completa_en_lugar_de_duplicarse()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            var areaQa = await e.Catalogo.CrearAreaAsync(admin, "QA", "Quality");
            await using (var db = e.Db())
            {
                db.SubAreas.Add(new SubArea { AreaId = areaQa, Nombre = "Metrología" }); // como las anteriores al estándar
                await db.SaveChangesAsync();
            }

            await Cargador(e).CargarAsync(admin);

            var qa = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Codigo == "QA");
            var met = Assert.Single(qa.SubAreas, s => s.Nombre == "Metrología");
            Assert.Equal("MET", met.Codigo);
            Assert.Equal(15, qa.SubAreas.Count);
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task No_sobrescribe_los_tipos_que_el_administrador_ya_definio_para_metrologia()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            var qa = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Codigo == "QA");
            var met = qa.SubAreas.Single(s => s.Codigo == "MET");
            var tipoF = (await e.Catalogo.ListarTiposAsync()).Single(t => t.Codigo == "F").Id;
            await e.Catalogo.EstablecerTiposDeSubAreaAsync(admin, met.Id, [tipoF]); // el Administrador decidió: solo Formatos

            await Cargador(e).CargarAsync(admin);

            Assert.Equal([tipoF], (await e.Catalogo.TiposDisponiblesAsync(qa.Id, met.Id)).Select(t => t.Id));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    // ---------- Reglas de tipos por unidad ----------

    [Fact]
    public async Task El_plan_de_inspeccion_no_aplica_en_metrologia_pero_si_en_las_demas_unidades_de_calidad()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            var qa = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Codigo == "QA");
            var met = qa.SubAreas.Single(s => s.Codigo == "MET");
            var ii = qa.SubAreas.Single(s => s.Codigo == "II");

            var enMet = (await e.Catalogo.TiposDisponiblesAsync(qa.Id, met.Id)).Select(t => t.Codigo).ToList();
            var enIi = (await e.Catalogo.TiposDisponiblesAsync(qa.Id, ii.Id)).Select(t => t.Codigo).ToList();

            Assert.DoesNotContain("PI", enMet);
            Assert.Contains("P", enMet);
            Assert.Equal(19, enMet.Count);
            Assert.Contains("PI", enIi);
            Assert.Equal(20, enIi.Count);
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Un_autor_no_puede_crear_un_plan_de_inspeccion_en_metrologia()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            var qa = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Codigo == "QA");
            var met = qa.SubAreas.Single(s => s.Codigo == "MET");
            var pi = (await e.Catalogo.ListarTiposAsync()).Single(t => t.Codigo == "PI").Id;
            var autor = await e.NuevoAutorAsync("QA");

            await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(autor, new NuevoDocumento("Plan", qa.Id, met.Id, pi)));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Cada_tipo_queda_con_el_flujo_revision_y_aprobacion()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            await using var db = e.Db();

            var pasos = await db.PasosPlantilla.Include(p => p.TipoDocumento).OrderBy(p => p.TipoDocumentoId).ThenBy(p => p.Orden).ToListAsync();

            Assert.Equal(40, pasos.Count);
            foreach (var g in pasos.GroupBy(p => p.TipoDocumentoId))
            {
                Assert.Equal([("Revisión", TipoPaso.Revision), ("Aprobación", TipoPaso.Aprobacion)], g.Select(p => (p.Nombre, p.Clase)));
            }
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task No_pisa_la_plantilla_de_flujo_que_un_tipo_ya_tenia()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            var tipoP = await e.Catalogo.CrearTipoAsync(admin, "P", "Procedimiento", 12);
            await e.Flujo.GuardarPlantillaAsync(admin, tipoP, [new(1, TipoPaso.Aprobacion, "Solo aprobación")]);

            var r = await Cargador(e).CargarAsync(admin);

            await using var db = e.Db();
            var pasos = await db.PasosPlantilla.Where(p => p.TipoDocumentoId == tipoP).ToListAsync();
            Assert.Equal("Solo aprobación", Assert.Single(pasos).Nombre);
            Assert.Equal(19, r.PlantillasNuevas);
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    // ---------- De punta a punta: el catálogo cargado sirve para crear documentos ----------

    [Fact]
    public async Task Con_el_catalogo_cargado_un_autor_crea_documentos_con_el_codigo_del_estandar_de_calidad()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            var areas = (await e.Catalogo.ListarAreasAsync()).ToDictionary(a => a.Codigo);
            var tipos = (await e.Catalogo.ListarTiposAsync()).ToDictionary(t => t.Codigo);
            var autor = await e.NuevoActorAsync([RolSistema.Autor], "QA", "PR", "MT");

            var formato = await e.Documentos.CrearAsync(autor, new("Inspección de recibo", areas["QA"].Id, areas["QA"].SubAreas.Single(s => s.Codigo == "II").Id, tipos["F"].Id));
            var otro = await e.Documentos.CrearAsync(autor, new("Otro formato", areas["QA"].Id, areas["QA"].SubAreas.Single(s => s.Codigo == "II").Id, tipos["F"].Id));
            var piQa = await e.Documentos.CrearAsync(autor, new("Plan de inspección palancas", areas["QA"].Id, areas["QA"].SubAreas.Single(s => s.Codigo == "DC").Id, tipos["PI"].Id));
            var avPr = await e.Documentos.CrearAsync(autor, new("Palanca de almacenamiento", areas["PR"].Id, areas["PR"].SubAreas.Single(s => s.Codigo == "DC").Id, tipos["AV"].Id));
            var piPr = await e.Documentos.CrearAsync(autor, new("Plan de producción", areas["PR"].Id, areas["PR"].SubAreas.Single(s => s.Codigo == "DC").Id, tipos["PI"].Id));
            var mt = await e.Documentos.CrearAsync(autor, new("Recepción", areas["MT"].Id, areas["MT"].SubAreas.Single(s => s.Codigo == "SIP").Id, tipos["P"].Id));

            Assert.Equal("QRT-F-QA-II-001", formato.Codigo);
            Assert.Equal("QRT-F-QA-II-002", otro.Codigo);
            Assert.Equal("QRT-PI-QA-DC-001", piQa.Codigo);
            Assert.Equal("QRT-AV-PR-DC-001", avPr.Codigo);
            Assert.Equal("QRT-PI-PR-DC-001", piPr.Codigo);                  // misma unidad DC, otra área: otra numeración
            Assert.Equal("QRT-P-MT-SIP-001", mt.Codigo);                    // tipos de una letra
            Assert.Equal("Incoming Inspection", formato.SubArea);
            Assert.Equal("Process Quality · Die Cast", piQa.SubArea);
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }

    [Fact]
    public async Task Un_autor_de_otra_area_no_crea_en_las_areas_cargadas()
    {
        var (bd, e, admin) = await LimpioAsync();
        try
        {
            await Cargador(e).CargarAsync(admin);
            var qa = (await e.Catalogo.ListarAreasAsync()).Single(a => a.Codigo == "QA");
            var f = (await e.Catalogo.ListarTiposAsync()).Single(t => t.Codigo == "F");
            var autorPr = await e.NuevoActorAsync([RolSistema.Autor], "PR");

            await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
                e.Documentos.CrearAsync(autorPr, new("No debería", qa.Id, qa.SubAreas.First().Id, f.Id)));
        }
        finally
        {
            await LiberarAsync(bd, e);
        }
    }
}
