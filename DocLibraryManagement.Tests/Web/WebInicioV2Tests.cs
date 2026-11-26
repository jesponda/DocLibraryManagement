using System.Net;
using System.Text.RegularExpressions;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// El front de la versión V2 (diseño de Calidad): Home con áreas, subáreas y nomenclatura, menú lateral, filtro por subárea
/// y archivos estáticos. Contra la aplicación real, con el catálogo sembrado (área CAL, unidad GEN, tipo PRO).
/// </summary>
public class WebInicioV2Tests(AplicacionWebFixture app) : IClassFixture<AplicacionWebFixture>
{
    private static string Texto(string html) =>
        Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private async Task<(HttpStatusCode Estado, string Html, string Texto)> PedirAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        var html = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, html, Texto(html));
    }

    // ---------- Home ----------

    [Fact]
    public async Task El_home_muestra_cada_area_con_su_abreviatura_sus_documentos_y_sus_subareas()
    {
        var (estado, html, texto) = await PedirAsync("lector", "/");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("¿Qué documentación buscas?", texto);
        Assert.Contains("Calidad", texto);
        Assert.Contains("CAL", texto);
        Assert.Contains("1 Doc", texto);                                   // el publicado; el programado, el de flujo y el de aprobación no cuentan
        Assert.Contains("Unidad general", texto);
        Assert.Contains($"lista-maestra?area={app.AreaId}", html);          // el nombre del área lleva a sus documentos
        var enlaces = string.Join(" | ", Regex.Matches(html, @"href=""lista-maestra\?area=[^""]*""").Select(m => m.Value));
        Assert.True(Regex.IsMatch(html, $@"lista-maestra\?area={app.AreaId}(&amp;|&#x26;|&)subarea={app.SubAreaId}"""), enlaces);
        Assert.DoesNotContain("subarea=0", html);
    }

    [Fact]
    public async Task El_home_muestra_la_nomenclatura_de_tipos_con_enlace_a_la_lista_maestra()
    {
        var (_, html, texto) = await PedirAsync("lector", "/");

        Assert.Contains("Nomenclatura · Tipo documental", texto);
        Assert.Matches(@"PRO\s+Procedimiento", texto);
        Assert.Matches(@"href=""lista-maestra\?tipo=\d+""", html);
    }

    [Fact]
    public async Task El_home_explica_como_se_forma_el_codigo_con_el_sitio_configurado()
    {
        var (_, _, texto) = await PedirAsync("lector", "/");

        Assert.Matches(@"QRT\s*-\s*TIPO\s*-\s*ÁREA\s*-\s*UNIDAD\s*-\s*NNN", texto);   // cada parte va en su propio elemento (con ayuda al pasar el cursor)
        Assert.Contains("La unidad es la subárea del documento", texto);
        Assert.Contains("no se reutiliza", texto);
    }

    [Fact]
    public async Task El_buscador_del_home_tiene_tipo_area_y_subarea_dependiente()
    {
        var (_, html, _) = await PedirAsync("lector", "/");

        Assert.Contains("id=\"inicio-area\"", html);
        Assert.Contains("data-depende=\"inicio-area\"", html);              // la subárea depende del área (lo resuelve sgd.js)
        Assert.Matches($@"<option value=""{app.SubAreaId}"" data-padre=""{app.AreaId}"">Unidad general \(GEN\)</option>", html);
    }

    [Fact]
    public async Task El_home_no_cuenta_borradores_ni_documentos_en_flujo()
    {
        var (_, _, texto) = await PedirAsync("lector", "/");

        Assert.DoesNotContain(app.CodigoDocumento, texto);
        Assert.DoesNotContain("Procedimiento de arranque", texto);
    }

    // ---------- Menú y encabezado ----------

    [Fact]
    public async Task El_menu_marca_la_opcion_activa_y_Documentos_no_se_marca_junto_a_Nuevo_documento()
    {
        var (_, enLista, _) = await PedirAsync("autor-cal", "/lista-maestra");
        var (_, enNuevo, _) = await PedirAsync("autor-cal", "/documentos/nuevo");
        var (_, enDetalle, _) = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Contains("class=\"sgd-nav active\" href=\"lista-maestra\"", enLista);
        Assert.DoesNotContain("class=\"sgd-nav active\" href=\"\"", enLista);
        Assert.Contains("class=\"sgd-nav active\" href=\"documentos/nuevo\"", enNuevo);
        Assert.Contains("class=\"sgd-nav\" href=\"documentos\"", enNuevo);   // «Documentos» NO se marca en «Nuevo documento»
        Assert.Contains("class=\"sgd-nav active\" href=\"documentos\"", enDetalle);
    }

    [Fact]
    public async Task El_encabezado_muestra_el_logo_y_las_iniciales_de_quien_inicio_sesion()
    {
        var (_, html, _) = await PedirAsync("autor-cal", "/");

        Assert.Contains("src=\"img/allegion.png\"", html);
        Assert.Contains("SGD", html);
        Assert.Contains("Allegion Querétaro", html);
        Assert.Matches(@"<div class=""avatar"" aria-hidden=""true"">AU</div>", html);   // DEV\autor-cal
    }

    [Fact]
    public async Task Quien_tiene_tareas_ve_cuantas_en_el_menu()
    {
        var (_, html, _) = await PedirAsync("revisor", "/");

        Assert.Matches(@"Mis tareas\s*<span class=""badge text-bg-warning"">\d+</span>", html); // el documento en flujo espera al revisor
    }

    // ---------- Lista maestra por subárea ----------

    [Fact]
    public async Task La_lista_maestra_filtra_por_subarea()
    {
        var (_, _, propia) = await PedirAsync("lector", $"/lista-maestra?area={app.AreaId}&subarea={app.SubAreaId}");
        var (_, _, ajena) = await PedirAsync("lector", $"/lista-maestra?area={app.AreaId}&subarea={app.SubAreaId + 999}");

        Assert.Contains(app.CodigoPublicado, propia);
        Assert.DoesNotContain(app.CodigoPublicado, ajena);
        Assert.Contains("0 documento(s)", ajena);
    }

    [Fact]
    public async Task La_lista_maestra_acepta_q_como_el_buscador_del_home()
    {
        var (_, _, porQ) = await PedirAsync("lector", $"/lista-maestra?q={Uri.EscapeDataString(app.CodigoPublicado)}");
        var (_, _, sinCoincidencia) = await PedirAsync("lector", "/lista-maestra?q=zzzzinexistente");

        Assert.Contains(app.CodigoPublicado, porQ);
        Assert.DoesNotContain(app.CodigoPublicado, sinCoincidencia);
    }

    [Fact]
    public async Task El_filtro_de_subarea_de_la_lista_ofrece_las_subareas_con_su_area_y_conserva_la_elegida()
    {
        var (_, html, _) = await PedirAsync("lector", $"/lista-maestra?area={app.AreaId}&subarea={app.SubAreaId}");

        Assert.Contains("data-depende=\"area\"", html);
        Assert.Matches($@"<option value=""{app.SubAreaId}"" data-padre=""{app.AreaId}"" selected>Unidad general \(GEN\)</option>", html);
    }

    [Fact]
    public async Task La_exportacion_de_la_lista_maestra_respeta_el_filtro_de_subarea()
    {
        using var cliente = await app.ClienteComoAsync("lector");
        var propia = await cliente.GetStringAsync($"/lista-maestra/exportar?formato=csv&subarea={app.SubAreaId}");
        var ajena = await cliente.GetStringAsync($"/lista-maestra/exportar?formato=csv&subarea={app.SubAreaId + 999}");
        var porQ = await cliente.GetStringAsync($"/lista-maestra/exportar?formato=csv&q={Uri.EscapeDataString(app.CodigoPublicado)}");

        Assert.Contains(app.CodigoPublicado, propia);
        Assert.DoesNotContain(app.CodigoPublicado, ajena);
        Assert.Contains(app.CodigoPublicado, porQ);
    }

    // ---------- Código del documento ----------

    [Fact]
    public async Task Los_documentos_sembrados_llevan_el_codigo_del_estandar_con_su_unidad()
    {
        var (_, _, texto) = await PedirAsync("lector", "/lista-maestra");

        Assert.Matches(@"QRT-PRO-CAL-GEN-\d{3}", app.CodigoPublicado);
        Assert.Contains(app.CodigoPublicado, texto);
    }

    // ---------- Administración ----------

    [Fact]
    public async Task Administracion_ofrece_cargar_el_catalogo_estandar_con_lo_que_trae()
    {
        var (_, _, texto) = await PedirAsync("admin", "/administracion");

        Assert.Contains("Catálogo estándar de Calidad", texto);
        Assert.Contains("9 áreas", texto);
        Assert.Contains("75 subáreas (unidades)", texto);
        Assert.Contains("20 tipos documentales", texto);
        Assert.Contains("Cargar catálogo estándar", texto);
        Assert.Contains("solo agrega", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task La_pantalla_de_nuevo_documento_carga_para_un_autor()
    {
        var (estado, _, texto) = await PedirAsync("autor-cal", "/documentos/nuevo");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Nuevo documento", texto);
    }

    [Fact]
    public async Task El_detalle_de_un_documento_dice_que_su_unidad_forma_parte_del_codigo()
    {
        var (_, _, texto) = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Contains("Subárea (unidad)", texto);
        Assert.Contains("Unidad general", texto);
        Assert.Contains("forma parte del código", texto);
    }

    // ---------- Archivos estáticos ----------

    [Fact]
    public async Task Los_archivos_del_front_se_sirven_sin_sesion_para_que_el_acceso_salga_con_estilo()
    {
        using var anonimo = app.CrearCliente();
        var html = await (await anonimo.GetAsync("/dev/login")).Content.ReadAsStringAsync();
        var css = Regex.Match(html, @"href=""(css/sgd-tema\.[^""]+\.css)""").Groups[1].Value;
        var js = Regex.Match(html, @"src=""(js/sgd\.[^""]+\.js)""").Groups[1].Value;

        Assert.NotEmpty(css);
        Assert.NotEmpty(js);
        foreach (var (ruta, tipo) in new[] { (css, "text/css"), (js, "javascript"), ("img/allegion.png", "image/png"), ("favicon.png", "image/png") })
        {
            using var r = await anonimo.GetAsync("/" + ruta);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains(tipo, r.Content.Headers.ContentType!.ToString());
        }
    }

    [Fact]
    public async Task Abrir_los_estaticos_no_abre_nada_mas_las_pantallas_y_los_archivos_siguen_pidiendo_sesion()
    {
        using var anonimo = app.CrearCliente();

        foreach (var ruta in new[] { "/", "/lista-maestra", "/reportes", "/auditoria", $"/archivos/{app.RevisionId}/pdf", "/lista-maestra/exportar" })
        {
            using var r = await anonimo.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/dev/login", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task El_tema_trae_la_paleta_de_calidad_y_el_modo_oscuro()
    {
        using var anonimo = app.CrearCliente();
        var html = await anonimo.GetStringAsync("/dev/login");
        var ruta = Regex.Match(html, @"href=""(css/sgd-tema\.[^""]+\.css)""").Groups[1].Value;

        var css = await anonimo.GetStringAsync("/" + ruta);

        Assert.Contains("#FD6E31", css);                                    // naranja Allegion
        Assert.Contains("prefers-color-scheme: dark", css);
        Assert.Contains("--navy", css);
    }
}
