using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// Fase 3 por HTTP, contra la aplicación real: lista maestra, búsqueda, visor y exportación, vistos como
/// los ve cada rol. Lo importante: un lector solo llega a lo vigente y nunca ve títulos de lo no publicado.
/// </summary>
public class WebConsultaTests(AplicacionWebFixture app) : IClassFixture<AplicacionWebFixture>
{
    private static string Texto(string html) =>
        Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private async Task<(HttpStatusCode Estado, string Html, string Texto, string? Ubicacion)> PedirAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        var html = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, html, Texto(html), r.Headers.Location?.ToString());
    }

    // ---------- Lista maestra ----------

    [Fact]
    public async Task Un_lector_ve_en_la_lista_maestra_solo_lo_vigente()
    {
        var (estado, _, t, _) = await PedirAsync("lector", "/lista-maestra");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains(app.CodigoPublicado, t);
        Assert.Contains(app.TituloPublicado, t);
        Assert.Contains("Rev.01", t);
        Assert.DoesNotContain(app.CodigoDocumento, t);          // en flujo
        Assert.DoesNotContain(app.CodigoEnAprobacion, t);       // esperando la última aprobación
        Assert.DoesNotContain("Procedimiento de arranque", t);  // ni su título
        Assert.DoesNotContain("Rev.00", t);                     // la obsoleta no está
    }

    [Theory]
    [InlineData("?texto=publicado", true)]
    [InlineData("?texto=PUBLICADO", true)]
    [InlineData("?texto=zzzzz", false)]
    public async Task El_filtro_de_texto_de_la_lista_funciona(string consulta, bool aparece)
    {
        var (_, _, t, _) = await PedirAsync("lector", "/lista-maestra" + consulta);

        Assert.Equal(aparece, t.Contains(app.CodigoPublicado));
        if (!aparece)
        {
            Assert.Contains("No hay documentos vigentes que coincidan", t);
        }
    }

    [Fact]
    public async Task El_filtro_por_area_excluye_a_las_demas()
    {
        var (_, _, otra, _) = await PedirAsync("lector", $"/lista-maestra?area={app.AreaId + 999}");
        var (_, _, propia, _) = await PedirAsync("lector", $"/lista-maestra?area={app.AreaId}");

        Assert.DoesNotContain(app.CodigoPublicado, otra);
        Assert.Contains(app.CodigoPublicado, propia);
    }

    [Fact]
    public async Task La_lista_enlaza_al_visor_estable_y_ofrece_exportar()
    {
        var (_, html, _, _) = await PedirAsync("lector", "/lista-maestra");

        Assert.Contains($"href=\"doc/{app.CodigoPublicado}\"", html);
        Assert.Contains("lista-maestra/exportar?formato=xlsx", html);
        Assert.Contains("lista-maestra/exportar?formato=csv", html);
    }

    [Fact]
    public async Task Los_enlaces_de_exportar_conservan_los_filtros()
    {
        var (_, html, _, _) = await PedirAsync("lector", $"/lista-maestra?texto=publicado&area={app.AreaId}");

        Assert.Contains("formato=csv", html);
        Assert.Contains("texto=publicado", html);
        Assert.Contains($"area={app.AreaId}", html);
    }

    [Fact]
    public async Task Un_texto_malicioso_en_el_filtro_no_se_inyecta_en_la_pagina()
    {
        var (_, html, _, _) = await PedirAsync("lector", "/lista-maestra?texto=%3Cscript%3Ealert(1)%3C%2Fscript%3E");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    // ---------- Exportación ----------

    [Fact]
    public async Task El_CSV_exportado_trae_solo_lo_vigente_con_BOM_y_nombre_de_archivo()
    {
        using var cliente = await app.ClienteComoAsync("lector");

        using var r = await cliente.GetAsync("/lista-maestra/exportar?formato=csv");
        var bytes = await r.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("text/csv", r.Content.Headers.ContentType!.MediaType);
        Assert.Matches(@"^ListaMaestra_\d{8}\.csv$", r.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var lineas = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lineas.Length);                       // encabezado + el único documento vigente
        Assert.StartsWith(app.CodigoPublicado, lineas[1]);
        Assert.DoesNotContain(app.CodigoDocumento, string.Join('\n', lineas));
    }

    [Fact]
    public async Task El_Excel_exportado_es_un_libro_valido_con_el_documento_vigente()
    {
        using var cliente = await app.ClienteComoAsync("lector");

        using var r = await cliente.GetAsync("/lista-maestra/exportar?formato=xlsx");
        var bytes = await r.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", r.Content.Headers.ContentType!.MediaType);
        using var libro = new XLWorkbook(new MemoryStream(bytes));
        var hoja = libro.Worksheet("Lista maestra");
        Assert.Equal(app.CodigoPublicado, hoja.Cell(2, 1).GetString());
        Assert.Equal("Rev.01", hoja.Cell(2, 6).GetString());
        Assert.Equal(2, hoja.LastRowUsed()!.RowNumber());
    }

    [Fact]
    public async Task La_exportacion_respeta_los_filtros_y_el_formato_por_defecto_es_Excel()
    {
        using var cliente = await app.ClienteComoAsync("lector");

        using var sinCoincidencia = await cliente.GetAsync("/lista-maestra/exportar?formato=csv&texto=zzzzz");
        using var porDefecto = await cliente.GetAsync("/lista-maestra/exportar");

        var csv = Encoding.UTF8.GetString(await sinCoincidencia.Content.ReadAsByteArrayAsync(), 3, (int)sinCoincidencia.Content.Headers.ContentLength! - 3);
        Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)); // solo el encabezado
        Assert.EndsWith(".xlsx", porDefecto.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
    }

    [Theory]
    [InlineData("/lista-maestra/exportar?formato=pdf")]
    [InlineData("/lista-maestra/exportar?formato=../../etc/passwd")]
    [InlineData("/lista-maestra/exportar?formato=csv&texto=a")]
    public async Task Un_formato_o_filtro_invalido_da_400_con_mensaje(string ruta)
    {
        using var cliente = await app.ClienteComoAsync("lector");

        using var r = await cliente.GetAsync(ruta);

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sin_sesion_no_se_exporta_ni_se_ve_la_lista()
    {
        using var cliente = app.CrearCliente();

        foreach (var ruta in new[] { "/lista-maestra", "/lista-maestra/exportar?formato=csv", "/buscar?q=algo", $"/doc/{app.CodigoPublicado}" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/dev/login", r.Headers.Location!.ToString());
        }
    }

    // ---------- Búsqueda ----------

    [Fact]
    public async Task La_pagina_de_busqueda_sin_consulta_muestra_el_formulario_y_ningun_resultado()
    {
        var (estado, _, t, _) = await PedirAsync("lector", "/buscar");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Buscar documentos", t);
        Assert.DoesNotContain("resultado(s)", t);
    }

    [Fact]
    public async Task Buscar_por_contenido_encuentra_el_documento_y_resalta_la_coincidencia()
    {
        var (_, html, t, _) = await PedirAsync("lector", "/buscar?q=CALIBRACION+balanzas");

        Assert.Contains("1 resultado(s)", t);
        Assert.Contains(app.CodigoPublicado, t);
        // Razor codifica las letras acentuadas como entidades (&#xF3;); el navegador las muestra bien, así que se decodifica.
        Assert.Contains("<mark>calibración</mark>", WebUtility.HtmlDecode(html)); // resaltado, conservando el acento del documento
        Assert.Contains($"href=\"doc/{app.CodigoPublicado}\"", html);
    }

    [Fact]
    public async Task Un_lector_no_encuentra_lo_que_dice_un_documento_no_publicado_ni_su_titulo()
    {
        var (_, _, porContenido, _) = await PedirAsync("lector", "/buscar?q=bloqueo+energia");
        var (_, _, porTitulo, _) = await PedirAsync("lector", "/buscar?q=arranque");

        Assert.Contains("No se encontró ningún documento", porContenido);
        Assert.DoesNotContain(app.CodigoDocumento, porContenido);
        Assert.Contains("No se encontró ningún documento", porTitulo);
        Assert.DoesNotContain("Procedimiento de arranque", porTitulo + porContenido);
    }

    [Fact]
    public async Task Un_lector_no_encuentra_lo_obsoleto_ni_siquiera_pidiendolo_en_la_URL()
    {
        var (_, _, normal, _) = await PedirAsync("lector", "/buscar?q=pesaje+tolerancia");
        var (_, _, forzado, _) = await PedirAsync("lector", "/buscar?q=pesaje+tolerancia&obsoletas=1");

        Assert.Contains("No se encontró ningún documento", normal);
        Assert.Contains("No se encontró ningún documento", forzado);
        Assert.DoesNotContain("Incluir revisiones obsoletas", normal); // la opción ni se le ofrece
    }

    [Theory]
    [InlineData("auditor")]
    [InlineData("admin")]
    public async Task Administrador_y_Auditor_pueden_buscar_en_obsoletas_y_se_les_marcan(string persona)
    {
        var (_, html, t, _) = await PedirAsync(persona, "/buscar?q=pesaje+tolerancia&obsoletas=1");

        Assert.Contains("Incluir revisiones obsoletas", t);
        Assert.Contains("1 resultado(s)", t);
        Assert.Contains("Obsoleta", t);
        Assert.Contains($"href=\"doc/{app.CodigoPublicado}/Rev.00\"", html); // el enlace va a la revisión concreta, no a la vigente
    }

    [Fact]
    public async Task Sin_marcar_obsoletas_ni_el_Auditor_las_ve()
    {
        var (_, _, t, _) = await PedirAsync("auditor", "/buscar?q=pesaje+tolerancia");

        Assert.Contains("No se encontró ningún documento", t);
    }

    [Fact]
    public async Task Una_consulta_de_un_solo_caracter_muestra_un_mensaje_y_no_falla()
    {
        var (estado, _, t, _) = await PedirAsync("lector", "/buscar?q=a");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Escribe al menos una palabra", t);
    }

    [Fact]
    public async Task Una_consulta_en_blanco_no_busca_nada_ni_muestra_error()
    {
        var (estado, _, t, _) = await PedirAsync("lector", "/buscar?q=%20%20");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.DoesNotContain("Escribe al menos una palabra", t);
        Assert.DoesNotContain("resultado(s)", t);
    }

    [Fact]
    public async Task El_texto_buscado_y_el_del_PDF_se_muestran_codificados_nunca_como_marcado()
    {
        var (_, html, _, _) = await PedirAsync("lector", "/buscar?q=%22%3E%3Cimg+src%3Dx+onerror%3Dalert(1)%3E");

        Assert.DoesNotContain("<img src=x onerror=alert(1)>", html);
    }

    // ---------- Visor ----------

    [Theory]
    [InlineData("lector")]
    [InlineData("revisor")]
    [InlineData("admin")]
    public async Task El_enlace_estable_muestra_el_PDF_vigente_a_cualquier_usuario(string persona)
    {
        var (estado, html, t, _) = await PedirAsync(persona, $"/doc/{app.CodigoPublicado}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains(app.TituloPublicado, t);
        Assert.Contains("Rev.01", t);
        Assert.Contains($"<iframe src=\"archivos/{app.RevisionPublicadaId}/pdf\"", html);
        Assert.DoesNotContain("OBSOLETO", t);
    }

    [Fact]
    public async Task El_codigo_del_enlace_no_distingue_mayusculas()
    {
        var (_, html, _, _) = await PedirAsync("lector", $"/doc/{app.CodigoPublicado.ToLowerInvariant()}");

        Assert.Contains($"archivos/{app.RevisionPublicadaId}/pdf", html);
    }

    [Theory]
    [InlineData("/doc/NOEXISTE-000")]
    public async Task Un_codigo_inexistente_da_un_mensaje_generico(string ruta)
    {
        var (estado, html, t, _) = await PedirAsync("lector", ruta);

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Documento no disponible", t);
        Assert.DoesNotContain("<iframe", html);
    }

    [Fact]
    public async Task Un_documento_no_publicado_da_el_mismo_mensaje_generico_sin_revelar_su_titulo()
    {
        var (_, html, t, _) = await PedirAsync("lector", $"/doc/{app.CodigoDocumento}");

        Assert.Contains("Documento no disponible", t);
        Assert.DoesNotContain("Procedimiento de arranque", t); // el lector no sabe ni cómo se llama
        Assert.DoesNotContain("<iframe", html);
    }

    [Fact]
    public async Task Un_lector_no_puede_abrir_una_revision_obsoleta_por_su_etiqueta()
    {
        var (estado, html, t, _) = await PedirAsync("lector", $"/doc/{app.CodigoPublicado}/Rev.00");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("No tienes permiso", t);
        Assert.DoesNotContain($"archivos/{app.RevisionObsoletaId}/pdf", html);
        Assert.DoesNotContain("pesaje", t);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("auditor")]
    [InlineData("autor-cal")]
    public async Task Los_gestores_ven_la_revision_obsoleta_con_el_banner_y_el_enlace_a_la_vigente(string persona)
    {
        var (estado, html, t, _) = await PedirAsync(persona, $"/doc/{app.CodigoPublicado}/Rev.00");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("OBSOLETO", t);
        Assert.Contains("No debe usarse", t);
        Assert.Contains($"href=\"doc/{app.CodigoPublicado}\"", html); // a la vigente
        Assert.Contains($"archivos/{app.RevisionObsoletaId}/pdf", html);
    }

    [Fact]
    public async Task La_etiqueta_de_la_vigente_tambien_funciona_para_un_lector()
    {
        var (_, html, t, _) = await PedirAsync("lector", $"/doc/{app.CodigoPublicado}/Rev.01");

        Assert.Contains(app.TituloPublicado, t);
        Assert.DoesNotContain("OBSOLETO", t);
        Assert.Contains($"archivos/{app.RevisionPublicadaId}/pdf", html);
    }

    [Theory]
    [InlineData("Rev.99")]
    [InlineData("loquesea")]
    public async Task Una_etiqueta_que_no_existe_da_el_mensaje_generico(string etiqueta)
    {
        var (_, _, t, _) = await PedirAsync("admin", $"/doc/{app.CodigoPublicado}/{etiqueta}");

        Assert.Contains("Documento no disponible", t);
    }

    [Fact]
    public async Task El_PDF_que_muestra_el_visor_se_sirve_en_linea_a_un_lector()
    {
        using var cliente = await app.ClienteComoAsync("lector");

        using var r = await cliente.GetAsync($"/archivos/{app.RevisionPublicadaId}/pdf");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.StartsWith("inline", r.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(app.ContenidoPdfVigente, await r.Content.ReadAsByteArrayAsync());
    }

    // ---------- Inicio, menú y gestión ----------

    [Fact]
    public async Task El_inicio_ofrece_buscar_y_la_lista_maestra_a_todos()
    {
        var (_, html, t, _) = await PedirAsync("lector", "/");

        Assert.Contains("action=\"buscar\"", html);
        Assert.Contains("Lista maestra", t);
        Assert.DoesNotContain("Mis tareas", t);
        Assert.DoesNotContain("Gestión de documentos", t);
    }

    [Fact]
    public async Task El_inicio_ofrece_a_cada_rol_su_atajo()
    {
        var (_, _, revisor, _) = await PedirAsync("revisor", "/");
        var (_, _, autor, _) = await PedirAsync("autor-cal", "/");

        Assert.Contains("Mis tareas", revisor);
        Assert.Contains("Gestión de documentos", autor);
        Assert.DoesNotContain("Mis tareas", autor);
    }

    [Fact]
    public async Task El_menu_incluye_lista_maestra_y_buscar_para_todos()
    {
        var (_, html, _, _) = await PedirAsync("lector", "/mi-acceso");

        Assert.Contains("href=\"lista-maestra\"", html);
        Assert.Contains("href=\"buscar\"", html);
    }

    [Fact]
    public async Task Desde_la_gestion_cada_revision_enlaza_a_su_visor()
    {
        var (_, html, _, _) = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Contains($"href=\"doc/{app.CodigoPublicado}/Rev.00\"", html);
        Assert.Contains($"href=\"doc/{app.CodigoPublicado}/Rev.01\"", html);
    }
}
