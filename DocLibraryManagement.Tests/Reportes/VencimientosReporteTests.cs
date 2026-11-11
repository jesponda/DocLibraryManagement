using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Reportes;

/// <summary>Reporte de documentos vencidos y por vencer: situación, filtros, alcance por rol y exportación.</summary>
public class VencimientosReporteTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private sealed record Cuatro(Escenario E, EscenarioFlujo F, string Vencido, string Urgente, string PorVencer, string AlDia);

    /// <summary>
    /// Cuatro documentos de una misma área, publicados en días distintos para que, con el reloj puesto un día después del
    /// primer vencimiento, haya uno en cada situación: vencido (-1), urgente (4 días), por vencer (19) y al día (59).
    /// </summary>
    private async Task<Cuatro> CuatroSituacionesAsync(bool conDueno = false)
    {
        var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        if (conDueno)
        {
            await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        }

        var t0 = e.Reloj.GetUtcNow();
        await e.PublicarPrimeraAsync(f);                                       // vence 2027-11-05

        e.Reloj.SetUtcNow(t0.AddDays(5));
        var (urgente, _) = await e.PublicarOtroAsync(f, "Urgente", "uno");     // vence 2027-11-10

        e.Reloj.SetUtcNow(t0.AddDays(20));
        var (porVencer, _) = await e.PublicarOtroAsync(f, "Por vencer", "dos"); // vence 2027-11-25

        e.Reloj.SetUtcNow(t0.AddDays(60));
        var (alDia, _) = await e.PublicarOtroAsync(f, "Al día", "tres");       // vence 2028-01-04

        e.Reloj.SetUtcNow(t0.AddMonths(12).AddDays(1));                         // un día después del primer vencimiento
        return new Cuatro(e, f, f.Documento.Codigo, urgente.Codigo, porVencer.Codigo, alDia.Codigo);
    }

    private static string[] Codigos(IEnumerable<FilaVencimientoDto> filas) => [.. filas.Select(f => f.Codigo)];

    // ---------- Situación ----------

    [Fact]
    public async Task Cada_documento_queda_clasificado_con_sus_dias_con_signo()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));

        var porCodigo = r.Filas.Elementos.ToDictionary(f => f.Codigo);
        Assert.Equal((EstadoVencimiento.Vencido, -1), (porCodigo[c.Vencido].Vencimiento, porCodigo[c.Vencido].Dias));
        Assert.Equal((EstadoVencimiento.PorVencer7, 4), (porCodigo[c.Urgente].Vencimiento, porCodigo[c.Urgente].Dias));
        Assert.Equal((EstadoVencimiento.PorVencer30, 19), (porCodigo[c.PorVencer].Vencimiento, porCodigo[c.PorVencer].Dias));
        Assert.Equal((EstadoVencimiento.AlDia, 59), (porCodigo[c.AlDia].Vencimiento, porCodigo[c.AlDia].Dias));
    }

    [Fact]
    public async Task Los_totales_cuentan_cada_situacion_sin_importar_el_filtro_de_estado()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        foreach (var estado in Enum.GetValues<FiltroEstadoVencimiento>())
        {
            var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(estado, c.F.AreaId));

            Assert.Equal(new TotalesVencimientos(1, 1, 1, 1), r.Totales);
            Assert.Equal(4, r.Totales.Total);
        }
    }

    [Theory]
    [InlineData(FiltroEstadoVencimiento.ConAviso, 3)]     // vencido + urgente + por vencer
    [InlineData(FiltroEstadoVencimiento.SoloVencidos, 1)]
    [InlineData(FiltroEstadoVencimiento.PorVencer, 2)]    // urgente + por vencer (todavía no vencidos)
    [InlineData(FiltroEstadoVencimiento.Todos, 4)]
    public async Task El_filtro_de_estado_deja_solo_lo_que_corresponde(FiltroEstadoVencimiento estado, int esperadas)
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(estado, c.F.AreaId));

        Assert.Equal(esperadas, r.Filas.Total);
        Assert.Equal(esperadas, r.Filas.Elementos.Count);
    }

    [Fact]
    public async Task Por_omision_se_ve_lo_que_requiere_atencion_ordenado_del_mas_atrasado_al_menos()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(AreaId: c.F.AreaId));

        Assert.Equal([c.Vencido, c.Urgente, c.PorVencer], Codigos(r.Filas.Elementos));
        Assert.DoesNotContain(c.AlDia, Codigos(r.Filas.Elementos));
    }

    [Fact]
    public async Task Solo_cuentan_las_revisiones_vigentes_de_documentos_activos()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var urgente = (await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId)))
            .Filas.Elementos.Single(f => f.Codigo == c.Urgente);

        await c.E.Revisiones.RetirarDocumentoAsync(c.F.Admin, urgente.DocumentoId, "Ya no aplica");

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));
        Assert.DoesNotContain(c.Urgente, Codigos(r.Filas.Elementos));
        Assert.Equal(3, r.Totales.Total);
    }

    [Fact]
    public async Task Una_revision_reemplazada_no_aparece_solo_la_vigente_actual()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var nueva = await c.E.Revisiones.CrearRevisionAsync(c.F.Autor, c.F.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await c.E.EnviarRevisionAsync(c.F, nueva.Id);
        await c.E.AprobarRevisionAsync(c.F, nueva.Id);   // Rev.01 vigente desde hoy: ya no está vencido

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));

        var fila = Assert.Single(r.Filas.Elementos, f => f.Codigo == c.Vencido);
        Assert.Equal("Rev.01", fila.Etiqueta);
        Assert.Equal(EstadoVencimiento.AlDia, fila.Vencimiento);
    }

    [Fact]
    public async Task La_fila_trae_responsable_dueno_y_si_hay_una_solicitud_pendiente()
    {
        var c = await CuatroSituacionesAsync(conDueno: true);
        using var _ = c.E;
        var antes = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(AreaId: c.F.AreaId));
        Assert.All(antes.Filas.Elementos, f => Assert.False(f.SolicitudPendiente));

        await c.E.Vencimientos.ProponerSinCambiosAsync(c.F.Autor, c.F.RevisionId, "Revisado y sigue vigente");

        var fila = Assert.Single((await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(AreaId: c.F.AreaId))).Filas.Elementos,
            f => f.Codigo == c.Vencido);
        Assert.True(fila.SolicitudPendiente);
        Assert.StartsWith("Usuario ", fila.Responsable);
        Assert.StartsWith("Usuario ", fila.Dueno);
        Assert.Contains("Procedimiento de prueba", fila.Titulo);
    }

    [Fact]
    public async Task Sin_dueno_de_area_la_fila_lo_indica_con_nulo()
    {
        var c = await CuatroSituacionesAsync(conDueno: false);
        using var _ = c.E;

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(AreaId: c.F.AreaId));

        Assert.All(r.Filas.Elementos, f => Assert.Null(f.Dueno));
    }

    // ---------- Filtros ----------

    [Fact]
    public async Task El_texto_filtra_por_codigo_y_por_titulo_sin_distinguir_acentos_ni_mayusculas()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var porTitulo = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId, Texto: "AL DIA"));
        var porCodigo = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId, Texto: c.Urgente.ToLowerInvariant()));

        Assert.Equal([c.AlDia], Codigos(porTitulo.Filas.Elementos));
        Assert.Equal([c.Urgente], Codigos(porCodigo.Filas.Elementos));
    }

    [Fact]
    public async Task El_texto_se_busca_literal_los_comodines_no_traen_todo()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var r = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId, Texto: "%%"));

        Assert.Empty(r.Filas.Elementos);
    }

    [Fact]
    public async Task El_filtro_de_texto_se_valida()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(Texto: new string('x', 201))));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(Texto: "a"))); // una sola letra coincidiría con casi todo
    }

    [Fact]
    public async Task El_filtro_de_tipo_y_de_area_acotan()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;

        var delTipo = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId, c.F.TipoId));
        var otroTipo = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId, int.MaxValue));
        var otraArea = await c.E.Reportes.VencimientosAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, int.MaxValue));

        Assert.Equal(4, delTipo.Filas.Total);
        Assert.Equal(0, otroTipo.Filas.Total);
        Assert.Equal(0, otraArea.Filas.Total);
        Assert.Equal(new TotalesVencimientos(0, 0, 0, 0), otroTipo.Totales);
    }

    [Fact]
    public async Task La_paginacion_reparte_las_filas_y_cuenta_el_total()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var filtro = new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId);

        var p1 = await c.E.Reportes.VencimientosAsync(c.F.Admin, filtro, pagina: 1, tamano: 3);
        var p2 = await c.E.Reportes.VencimientosAsync(c.F.Admin, filtro, pagina: 2, tamano: 3);

        Assert.Equal(3, p1.Filas.Elementos.Count);
        Assert.Single(p2.Filas.Elementos);
        Assert.Equal(4, p1.Filas.Total);
        Assert.Equal(2, p1.Filas.TotalPaginas);
        Assert.Equal([c.Vencido, c.Urgente, c.PorVencer, c.AlDia], Codigos(p1.Filas.Elementos.Concat(p2.Filas.Elementos)));
    }

    // ---------- Alcance por rol ----------

    [Fact]
    public async Task Administrador_y_auditor_ven_todas_las_areas()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var auditor = await c.E.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var actor in new[] { c.F.Admin, auditor })
        {
            var r = await c.E.Reportes.VencimientosAsync(actor, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));
            Assert.Equal(4, r.Filas.Total);
        }
    }

    [Fact]
    public async Task Un_autor_ve_solo_las_areas_donde_edita()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var autorOtraArea = await c.E.NuevoAutorAsync(Escenario.CodigoUnico("Z"));

        var propio = await c.E.Reportes.VencimientosAsync(c.F.Autor, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));
        var ajeno = await c.E.Reportes.VencimientosAsync(autorOtraArea, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));

        Assert.Equal(4, propio.Filas.Total);
        Assert.Equal(0, ajeno.Filas.Total);                          // pedir un área ajena no filtra nada hacia afuera
        Assert.Equal(0, ajeno.Totales.Total);
    }

    [Fact]
    public async Task Sin_filtro_de_area_un_autor_solo_recibe_sus_areas()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var autorOtraArea = await c.E.NuevoAutorAsync(Escenario.CodigoUnico("Z"));

        var r = await c.E.Reportes.VencimientosAsync(autorOtraArea, new FiltroVencimientos(FiltroEstadoVencimiento.Todos));

        Assert.Empty(Codigos(r.Filas.Elementos).Intersect([c.Vencido, c.Urgente, c.PorVencer, c.AlDia]));
    }

    [Fact]
    public async Task El_dueno_del_area_ve_su_area_y_solo_mientras_conserve_el_rol_de_aprobador()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var dueno = await c.E.NuevoAprobadorAsync();
        await c.E.Catalogo.EstablecerDuenoDeAreaAsync(c.F.Admin, c.F.AreaId, await c.E.IdDeAsync(dueno));
        var sinRol = dueno with { Roles = new HashSet<RolSistema> { RolSistema.Lector } };

        var r = await c.E.Reportes.VencimientosAsync(dueno, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId));

        Assert.Equal(4, r.Filas.Total);
        await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
            c.E.Reportes.VencimientosAsync(sinRol, new FiltroVencimientos(FiltroEstadoVencimiento.Todos, c.F.AreaId)));
    }

    [Fact]
    public async Task Quien_no_tiene_ningun_area_no_tiene_reporte()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var aprobadorNoDueno = await c.E.NuevoAprobadorAsync();
        var autorSinAreas = await c.E.NuevoAutorAsync();
        var lector = await c.E.NuevoActorAsync([]);

        foreach (var actor in new[] { aprobadorNoDueno, autorSinAreas, lector, c.F.Revisor })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => c.E.Reportes.VencimientosAsync(actor, new FiltroVencimientos()));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => c.E.Reportes.VencimientosCompletoAsync(actor, new FiltroVencimientos()));
        }
    }

    // ---------- Exportación ----------

    [Fact]
    public async Task La_lista_para_exportar_trae_lo_mismo_que_la_pantalla_y_respeta_el_alcance()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var filtro = new FiltroVencimientos(FiltroEstadoVencimiento.ConAviso, c.F.AreaId);
        var autorOtraArea = await c.E.NuevoAutorAsync(Escenario.CodigoUnico("Z"));

        var pantalla = await c.E.Reportes.VencimientosAsync(c.F.Admin, filtro);
        var exportada = await c.E.Reportes.VencimientosCompletoAsync(c.F.Admin, filtro);
        var deAjeno = await c.E.Reportes.VencimientosCompletoAsync(autorOtraArea, filtro);

        Assert.Equal(Codigos(pantalla.Filas.Elementos), Codigos(exportada));
        Assert.Empty(deAjeno);
    }

    [Fact]
    public async Task La_tabla_exportada_lleva_los_dias_negativos_como_numero_y_el_estado_en_espanol()
    {
        var c = await CuatroSituacionesAsync();
        using var _ = c.E;
        var filas = await c.E.Reportes.VencimientosCompletoAsync(c.F.Admin, new FiltroVencimientos(FiltroEstadoVencimiento.SoloVencidos, c.F.AreaId));

        var csv = System.Text.Encoding.UTF8.GetString(c.E.Exportador.ACsv(TablasReporte.Vencimientos(filas)));

        Assert.Contains("Vencida", csv);
        Assert.Contains(",-1,", csv);           // -1 día, sin apóstrofo
        Assert.DoesNotContain("'-1", csv);
    }
}
