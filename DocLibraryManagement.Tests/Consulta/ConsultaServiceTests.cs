using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Consulta;

public class ConsultaServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static ConsultaBusqueda Busca(string texto, EscenarioFlujo f, bool obsoletas = false) =>
        new(texto, AreaId: f.AreaId, IncluirObsoletos: obsoletas);

    // ---------- Lista maestra ----------

    [Fact]
    public async Task La_lista_maestra_solo_muestra_la_revision_vigente_de_documentos_activos()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);                                                          // vigente
        await e.Documentos.CrearAsync(f.Autor, new("Solo borrador", f.AreaId, null, f.TipoId));   // borrador
        var (retirado, _) = await e.PublicarOtroAsync(f, "Será retirado", "x");                    // vigente que luego se retira
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, retirado.Id, "baja");

        var pagina = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId));

        var fila = Assert.Single(pagina.Elementos);
        Assert.Equal(f.Documento.Codigo, fila.Codigo);
        Assert.Equal(1, pagina.Total);
    }

    [Fact]
    public async Task Cada_fila_trae_revision_fechas_area_tipo_y_responsable()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        var fila = Assert.Single((await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId))).Elementos);

        var ahora = e.Reloj.GetUtcNow().UtcDateTime;
        Assert.Equal("Rev.00", fila.Revision);
        Assert.Equal("Procedimiento de prueba", fila.Titulo);
        Assert.StartsWith(f.CodigoArea, fila.Area);
        Assert.StartsWith("Tipo ", fila.Tipo);
        Assert.Equal(ahora, fila.FechaAprobacionUtc);
        Assert.Equal(ahora, fila.FechaVigenciaUtc);
        Assert.Equal(ahora.AddMonths(12), fila.FechaProximaRevisionUtc);
        Assert.False(string.IsNullOrEmpty(fila.Responsable));
        Assert.Equal(f.Documento.Id, fila.DocumentoId);
    }

    [Fact]
    public async Task Al_publicar_una_revision_nueva_la_lista_muestra_solo_esa_y_no_la_obsoleta()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id);

        var fila = Assert.Single((await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId))).Elementos);

        Assert.Equal("Rev.01", fila.Revision);
        Assert.Equal(nueva.Id, fila.RevisionId);
    }

    [Fact]
    public async Task Una_revision_aprobada_con_vigencia_futura_aun_no_aparece_y_sigue_la_anterior()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id, e.Reloj.GetUtcNow().UtcDateTime.AddDays(10));

        var fila = Assert.Single((await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId))).Elementos);

        Assert.Equal("Rev.00", fila.Revision); // la que está en uso hasta que llegue la fecha
    }

    [Fact]
    public async Task Filtra_por_tipo_y_por_subarea()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var otroTipo = await e.Catalogo.CrearTipoAsync(f.Admin, Escenario.CodigoUnico("T"), "Otro tipo", 12);
        await e.Catalogo.EstablecerTiposDeAreaAsync(f.Admin, f.AreaId, [f.TipoId, otroTipo]);
        await e.Flujo.GuardarPlantillaAsync(f.Admin, otroTipo, [new(1, TipoPaso.Aprobacion, "Aprobación")]);
        var sub = await e.Catalogo.CrearSubAreaAsync(f.Admin, f.AreaId, "Línea 1");
        await e.PublicarPrimeraAsync(f);                                                           // tipo original, sin subárea
        var otro = await e.Documentos.CrearAsync(f.Autor, new("Del otro tipo", f.AreaId, sub, otroTipo));
        await e.SubirPdfAsync(f.Autor, otro.Revisiones.Single().Id, "otro");
        await e.Flujo.IniciarAsync(f.Autor, otro.Revisiones.Single().Id, [new(1, f.AprobadorId)]);
        await e.AprobarRevisionAsync(f, otro.Revisiones.Single().Id);

        var porTipo = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId, TipoDocumentoId: otroTipo));
        var porSub = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId, SubAreaId: sub));
        var todos = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId));

        Assert.Equal(otro.Codigo, Assert.Single(porTipo.Elementos).Codigo);
        Assert.Equal(otro.Codigo, Assert.Single(porSub.Elementos).Codigo);
        Assert.Equal("Línea 1", porSub.Elementos[0].SubArea);
        Assert.Equal(2, todos.Total);
    }

    [Theory]
    [InlineData("PROCEDIMIENTO")]
    [InlineData("procedimiento")]
    [InlineData("Procedimiento de prueba")]
    [InlineData("prueba procedimiento")]
    public async Task El_texto_filtra_por_titulo_sin_distinguir_mayusculas(string texto)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        var r = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(texto, f.AreaId));

        Assert.Single(r.Elementos);
    }

    [Fact]
    public async Task El_texto_tambien_filtra_por_codigo_y_no_distingue_acentos()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarOtroAsync(f, "Calibración de balanzas", "x");
        await e.PublicarPrimeraAsync(f);

        var porCodigo = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(f.Documento.Codigo.ToLowerInvariant(), f.AreaId));
        var porAcento = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra("CALIBRACION", f.AreaId));

        Assert.Equal(f.Documento.Codigo, Assert.Single(porCodigo.Elementos).Codigo);
        Assert.Equal("Calibración de balanzas", Assert.Single(porAcento.Elementos).Titulo);
    }

    [Fact]
    public async Task Los_comodines_del_usuario_se_buscan_literales_en_la_lista_maestra()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarOtroAsync(f, "Ajuste al 100% del flujo", "x");
        await e.PublicarOtroAsync(f, "Ajuste al 1000 del flujo", "x");

        var r = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra("100%", f.AreaId));

        Assert.Equal("Ajuste al 100% del flujo", Assert.Single(r.Elementos).Titulo); // «%» no es comodín
    }

    [Fact]
    public async Task La_lista_se_pagina_ordenada_por_codigo_y_el_total_es_exacto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        for (var i = 0; i < 4; i++)
        {
            await e.PublicarOtroAsync(f, $"Documento {i}", "x");
        }

        var p1 = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId), pagina: 1, tamano: 2);
        var p2 = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId), pagina: 2, tamano: 2);
        var p3 = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId), pagina: 3, tamano: 2);
        var p4 = await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId), pagina: 4, tamano: 2);

        Assert.Equal(5, p1.Total);
        Assert.Equal(3, p1.TotalPaginas);
        var codigos = p1.Elementos.Concat(p2.Elementos).Concat(p3.Elementos).Select(x => x.Codigo).ToList();
        Assert.Equal(5, codigos.Distinct().Count());
        Assert.Equal(codigos.OrderBy(c => c, StringComparer.Ordinal), codigos);
        Assert.Single(p3.Elementos);
        Assert.Empty(p4.Elementos);
    }

    [Fact]
    public async Task La_exportacion_trae_todo_lo_filtrado_sin_paginar()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        for (var i = 0; i < 30; i++)
        {
            await e.PublicarOtroAsync(f, $"Documento {i:00}", "x");
        }

        var completa = await e.Consulta().ListaMaestraCompletaAsync(new FiltroListaMaestra(AreaId: f.AreaId));

        Assert.Equal(30, completa.Count); // más que una página de 25
    }

    // ---------- Búsqueda ----------

    [Fact]
    public async Task Encuentra_un_documento_por_lo_que_dice_su_PDF_sin_distinguir_mayusculas_ni_acentos()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"Procedimiento de calibración con {palabra}");
        await e.PublicarPrimeraAsync(f);
        var lector = await e.NuevoActorAsync([]);

        var porContenido = await e.Consulta().BuscarAsync(lector, Busca($"CALIBRACION {palabra.ToUpperInvariant()}", f));

        var r = Assert.Single(porContenido.Elementos);
        Assert.Equal(f.Documento.Codigo, r.Codigo);
        Assert.False(r.CoincideEnCodigoOTitulo);
        Assert.Contains(r.Fragmento, s => s.Resaltado && s.Texto.Equals("calibración", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.Fragmento, s => s.Resaltado && s.Texto.Equals(palabra, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Exige_que_aparezcan_todas_las_palabras()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var a = Escenario.PalabraUnica();
        var b = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"habla de {a} nada más");
        await e.PublicarPrimeraAsync(f);
        var lector = await e.NuevoActorAsync([]);

        Assert.Single((await e.Consulta().BuscarAsync(lector, Busca(a, f))).Elementos);
        Assert.Empty((await e.Consulta().BuscarAsync(lector, Busca($"{a} {b}", f))).Elementos); // falta una palabra
    }

    [Fact]
    public async Task Busca_tambien_en_titulo_y_codigo_y_los_pone_primero()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        // Uno solo lo menciona en el contenido; el otro la tiene en el título.
        var (soloContenido, _) = await e.PublicarOtroAsync(f, "Un documento cualquiera", $"menciona {palabra} de pasada");
        var (enTitulo, _) = await e.PublicarOtroAsync(f, $"Guía de {palabra}", "texto sin relación");
        await e.SubirPdfAsync(f.Autor, f.RevisionId, "borrador");
        var lector = await e.NuevoActorAsync([]);

        var r = (await e.Consulta().BuscarAsync(lector, Busca(palabra, f))).Elementos;

        Assert.Equal([enTitulo.Codigo, soloContenido.Codigo], r.Select(x => x.Codigo));
        Assert.True(r[0].CoincideEnCodigoOTitulo);
        Assert.False(r[1].CoincideEnCodigoOTitulo);
        // Por código:
        var porCodigo = await e.Consulta().BuscarAsync(lector, Busca(enTitulo.Codigo.ToLowerInvariant(), f));
        Assert.Equal(enTitulo.Codigo, Assert.Single(porCodigo.Elementos).Codigo);
    }

    [Fact]
    public async Task Lo_que_dice_un_borrador_no_se_puede_buscar()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"contenido reservado {palabra}"); // sigue en borrador
        var lector = await e.NuevoActorAsync([]);

        Assert.Empty((await e.Consulta().BuscarAsync(lector, Busca(palabra, f))).Elementos);
    }

    [Fact]
    public async Task Una_revision_nueva_en_borrador_no_expone_su_contenido_y_la_vigente_sigue_encontrandose()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var vieja = Escenario.PalabraUnica();
        var nueva = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"vigente {vieja}");
        await e.PublicarPrimeraAsync(f);
        var rev1 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.SubirPdfAsync(f.Autor, rev1.Id, $"borrador {nueva}");
        var lector = await e.NuevoActorAsync([]);

        Assert.Single((await e.Consulta().BuscarAsync(lector, Busca(vieja, f))).Elementos);
        Assert.Empty((await e.Consulta().BuscarAsync(lector, Busca(nueva, f))).Elementos);
    }

    [Fact]
    public async Task Al_publicar_la_nueva_revision_se_busca_su_contenido_y_ya_no_el_de_la_obsoleta()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var vieja = Escenario.PalabraUnica();
        var nueva = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"texto {vieja}");
        await e.PublicarPrimeraAsync(f);
        var rev1 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, rev1.Id, $"texto {nueva}");
        await e.AprobarRevisionAsync(f, rev1.Id);
        var lector = await e.NuevoActorAsync([]);

        Assert.Single((await e.Consulta().BuscarAsync(lector, Busca(nueva, f))).Elementos);
        Assert.Empty((await e.Consulta().BuscarAsync(lector, Busca(vieja, f))).Elementos); // lo obsoleto no es de lectura abierta
    }

    [Fact]
    public async Task Los_obsoletos_los_incluyen_solo_Administrador_y_Auditor()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var vieja = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"texto {vieja}");
        await e.PublicarPrimeraAsync(f);
        var rev1 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, rev1.Id, "otro contenido");
        await e.AprobarRevisionAsync(f, rev1.Id);
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        var lector = await e.NuevoActorAsync([]);

        var delAuditor = await e.Consulta().BuscarAsync(auditor, Busca(vieja, f, obsoletas: true));
        var delAdmin = await e.Consulta().BuscarAsync(f.Admin, Busca(vieja, f, obsoletas: true));

        var r = Assert.Single(delAuditor.Elementos);
        Assert.Equal(EstadoRevision.Obsoleto, r.Estado);
        Assert.Equal("Rev.00", r.Revision);
        Assert.Single(delAdmin.Elementos);
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Consulta().BuscarAsync(lector, Busca(vieja, f, obsoletas: true)));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Consulta().BuscarAsync(f.Autor, Busca(vieja, f, obsoletas: true)));
    }

    [Fact]
    public async Task Un_documento_retirado_no_se_encuentra_para_lectores_pero_si_para_auditoria()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"texto {palabra}");
        await e.PublicarPrimeraAsync(f);
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja");
        var lector = await e.NuevoActorAsync([]);

        Assert.Empty((await e.Consulta().BuscarAsync(lector, Busca(palabra, f))).Elementos);
        Assert.Single((await e.Consulta().BuscarAsync(f.Admin, Busca(palabra, f, obsoletas: true))).Elementos);
    }

    [Fact]
    public async Task Los_comodines_se_buscan_literales_en_el_contenido()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarOtroAsync(f, "Uno", "ajuste al 50% del flujo");
        await e.PublicarOtroAsync(f, "Dos", "ajuste al 500 del flujo");
        await e.PublicarOtroAsync(f, "Tres", "valor a_b definido");
        await e.PublicarOtroAsync(f, "Cuatro", "valor axb definido");
        var lector = await e.NuevoActorAsync([]);

        var porcentaje = await e.Consulta().BuscarAsync(lector, Busca("50%", f));
        var guion = await e.Consulta().BuscarAsync(lector, Busca("a_b", f));

        Assert.Equal("Uno", Assert.Single(porcentaje.Elementos).Titulo);
        Assert.Equal("Tres", Assert.Single(guion.Elementos).Titulo);
    }

    [Fact]
    public async Task Filtra_la_busqueda_por_area_y_tipo()
    {
        using var e = new Escenario(bd);
        var f1 = await e.PrepararFlujoAsync(conPdf: false);
        var f2 = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f1.Autor, f1.RevisionId, $"texto {palabra}");
        await e.SubirPdfAsync(f2.Autor, f2.RevisionId, $"texto {palabra}");
        await e.PublicarPrimeraAsync(f1);
        await e.PublicarPrimeraAsync(f2);
        var lector = await e.NuevoActorAsync([]);

        var enAreaUno = await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(palabra, AreaId: f1.AreaId));
        var enTipoDos = await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(palabra, TipoDocumentoId: f2.TipoId));
        var sinFiltro = await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(palabra));

        Assert.Equal(f1.Documento.Codigo, Assert.Single(enAreaUno.Elementos).Codigo);
        Assert.Equal(f2.Documento.Codigo, Assert.Single(enTipoDos.Elementos).Codigo);
        Assert.Equal(2, sinFiltro.Total);
    }

    [Fact]
    public async Task La_busqueda_se_pagina()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var palabra = Escenario.PalabraUnica();
        for (var i = 0; i < 5; i++)
        {
            await e.PublicarOtroAsync(f, $"Documento {i}", $"contiene {palabra}");
        }

        var lector = await e.NuevoActorAsync([]);
        var p1 = await e.Consulta().BuscarAsync(lector, Busca(palabra, f), pagina: 1, tamano: 2);
        var p3 = await e.Consulta().BuscarAsync(lector, Busca(palabra, f), pagina: 3, tamano: 2);

        Assert.Equal(5, p1.Total);
        Assert.Equal(3, p1.TotalPaginas);
        Assert.Equal(2, p1.Elementos.Count);
        Assert.Single(p3.Elementos);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    public async Task Una_consulta_vacia_o_demasiado_corta_se_rechaza_con_mensaje(string texto)
    {
        using var e = new Escenario(bd);
        var lector = await e.NuevoActorAsync([]);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(texto)));
    }

    [Fact]
    public async Task Un_texto_con_codigo_SQL_se_trata_como_texto_y_no_rompe_nada()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var lector = await e.NuevoActorAsync([]);

        var r = await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda("'; DROP TABLE Documentos; --"));

        Assert.Empty(r.Elementos);
        Assert.NotEmpty((await e.Consulta().ListaMaestraAsync(new FiltroListaMaestra(AreaId: f.AreaId))).Elementos); // la tabla sigue ahí
    }

    // ---------- Visor ----------

    [Fact]
    public async Task El_enlace_estable_lleva_a_la_vigente_sin_importar_mayusculas_en_el_codigo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        var v = await e.Consulta().ObtenerVigenteAsync(f.Documento.Codigo.ToLowerInvariant());

        Assert.NotNull(v);
        Assert.Equal(f.RevisionId, v.RevisionId);
        Assert.Equal("Rev.00", v.Revision);
        Assert.Equal(f.Documento.Codigo, v.Codigo);
    }

    [Fact]
    public async Task El_enlace_estable_sigue_a_la_revision_nueva_cuando_se_publica()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id);

        var v = await e.Consulta().ObtenerVigenteAsync(f.Documento.Codigo);

        Assert.Equal(nueva.Id, v!.RevisionId);
        Assert.Equal("Rev.01", v.Revision);
    }

    [Fact]
    public async Task No_hay_vigente_para_un_borrador_un_codigo_inexistente_ni_un_documento_retirado()
    {
        using var e = new Escenario(bd);
        var borrador = await e.PrepararFlujoAsync();
        var retirado = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(retirado);
        await e.Revisiones.RetirarDocumentoAsync(retirado.Admin, retirado.Documento.Id, "baja");

        Assert.Null(await e.Consulta().ObtenerVigenteAsync(borrador.Documento.Codigo));
        Assert.Null(await e.Consulta().ObtenerVigenteAsync("NOEXISTE-000"));
        Assert.Null(await e.Consulta().ObtenerVigenteAsync(retirado.Documento.Codigo));
        Assert.Null(await e.Consulta().ObtenerVigenteAsync(""));
        Assert.Null(await e.Consulta().ObtenerVigenteAsync("   "));
    }

    [Theory]
    [InlineData("Rev.00", true)]
    [InlineData("rev.00", true)]
    [InlineData("REV.00", true)]
    [InlineData("Rev.0", true)]
    [InlineData("Rev.01", false)]
    [InlineData("Rev.00.1", false)]
    [InlineData("Rev.", false)]
    [InlineData("Rev.00.1.2", false)]
    [InlineData("xyz", false)]
    [InlineData("", false)]
    public async Task Resuelve_una_revision_por_su_etiqueta(string etiqueta, bool existe)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var r = await e.Consulta().ResolverRevisionAsync(f.Documento.Codigo, etiqueta);

        Assert.Equal(existe, r is not null);
        if (existe)
        {
            Assert.Equal((f.Documento.Id, f.RevisionId), r!.Value);
        }
    }

    [Fact]
    public async Task Resuelve_una_menor_y_no_confunde_una_cancelada_cuyo_numero_se_reutilizo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var primero = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Intento", null, false);
        await e.Revisiones.CancelarBorradorAsync(f.Autor, primero.Id, "no");
        var segundo = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Otro intento", null, false);

        var r = await e.Consulta().ResolverRevisionAsync(f.Documento.Codigo, "Rev.01");

        Assert.Equal(segundo.Id, r!.Value.RevisionId); // la cancelada (mismo número) queda fuera
        Assert.NotEqual(primero.Id, r.Value.RevisionId);
    }

    [Fact]
    public async Task Resolver_no_confunde_codigos_de_documentos_distintos()
    {
        using var e = new Escenario(bd);
        var a = await e.PrepararFlujoAsync();
        var b = await e.PrepararFlujoAsync();

        var ra = await e.Consulta().ResolverRevisionAsync(a.Documento.Codigo, "Rev.00");
        var rb = await e.Consulta().ResolverRevisionAsync(b.Documento.Codigo, "Rev.00");

        Assert.Equal(a.RevisionId, ra!.Value.RevisionId);
        Assert.Equal(b.RevisionId, rb!.Value.RevisionId);
    }
}
