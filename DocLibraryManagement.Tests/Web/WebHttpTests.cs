using System.Net;
using System.Text.RegularExpressions;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// Pruebas contra la aplicación real por HTTP. Las pantallas interactivas se generan en el servidor
/// al pedirlas (prerenderizado), así que aquí se comprueba su contenido y sus permisos; los clics
/// dentro de la página no se ejercitan (eso requiere un navegador).
/// </summary>
public class WebHttpTests(AplicacionWebFixture app) : IClassFixture<AplicacionWebFixture>
{
    private static string Texto(string html) =>
        Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private async Task<(HttpStatusCode Estado, string? Redireccion, string Texto)> PedirAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        return (r.StatusCode, r.Headers.Location?.ToString(), Texto(await r.Content.ReadAsStringAsync()));
    }

    private async Task<string> HtmlAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        return await r.Content.ReadAsStringAsync();
    }

    // ---------- Acceso a pantallas ----------

    [Fact]
    public async Task Sin_sesion_todo_redirige_al_acceso()
    {
        using var cliente = app.CrearCliente();

        foreach (var ruta in new[] { "/", "/documentos", "/pendientes", "/administracion", $"/archivos/{app.RevisionId}/pdf" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/dev/login", r.Headers.Location!.ToString());
        }
    }

    [Theory]
    [InlineData("admin", "/documentos", true)]
    [InlineData("autor-cal", "/documentos", true)]
    [InlineData("auditor", "/documentos", true)]
    [InlineData("lector", "/documentos", false)]
    [InlineData("revisor", "/pendientes", true)]
    [InlineData("aprobador", "/pendientes", true)]
    [InlineData("lector", "/pendientes", false)]
    [InlineData("autor-cal", "/pendientes", false)]
    [InlineData("admin", "/administracion/flujos", true)]
    [InlineData("autor-cal", "/administracion/flujos", false)]
    [InlineData("revisor", "/administracion/flujos", false)]
    [InlineData("admin", "/administracion/areas", true)]
    [InlineData("auditor", "/administracion/tipos", false)]
    [InlineData("admin", "/administracion/usuarios", true)]
    [InlineData("auditor", "/administracion/usuarios", false)]
    [InlineData("autor-cal", "/administracion/usuarios", false)]
    [InlineData("lector", "/administracion/usuarios", false)]
    [InlineData("admin", "/administracion/correo", true)]
    [InlineData("auditor", "/administracion/correo", false)]
    [InlineData("aprobador", "/administracion/correo", false)]
    [InlineData("lector", "/administracion/correo", false)]
    [InlineData("autor-cal", "/documentos/nuevo", true)]
    [InlineData("auditor", "/documentos/nuevo", false)]
    [InlineData("lector", "/documentos/nuevo", false)]
    public async Task Las_pantallas_respetan_el_rol(string persona, string ruta, bool permitido)
    {
        var (estado, redireccion, _) = await PedirAsync(persona, ruta);

        if (permitido)
        {
            Assert.Equal(HttpStatusCode.OK, estado);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, estado);
            Assert.Contains("/acceso-denegado", redireccion);
        }
    }

    [Theory]
    [InlineData("admin", true)]
    [InlineData("autor-cal", true)]    // es el responsable del documento publicado
    [InlineData("auditor", true)]
    [InlineData("revisor", false)]     // puede abrir el documento por tener una etapa, pero no participa de su revisión periódica
    public async Task El_panel_de_revision_periodica_se_muestra_solo_a_quien_participa(string persona, bool visible)
    {
        var (estado, _, texto) = await PedirAsync(persona, $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Equal(visible, texto.Contains("Revisión periódica"));
        if (visible)
        {
            Assert.Contains("Al día", texto); // el documento sembrado vence dentro de un año
        }
    }

    [Fact]
    public async Task Un_documento_en_borrador_sin_vigente_no_muestra_el_panel_de_revision_periodica()
    {
        var (_, _, texto) = await PedirAsync("admin", $"/documentos/{app.DocumentoId}");

        Assert.DoesNotContain("Revisión periódica", texto);
    }

    [Fact]
    public async Task La_bandeja_de_tareas_carga_con_las_dos_secciones_para_el_aprobador()
    {
        var (estado, _, texto) = await PedirAsync("aprobador", "/pendientes");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Mis tareas", texto);
        Assert.Contains(app.CodigoEnAprobacion, texto); // la tarea de aprobación sembrada sigue apareciendo
    }

    [Fact]
    public async Task La_lista_maestra_no_marca_como_vencido_lo_que_vence_dentro_de_un_ano()
    {
        var (_, _, texto) = await PedirAsync("lector", "/lista-maestra");

        Assert.Contains(app.CodigoPublicado, texto);
        Assert.DoesNotContain("Vencida", texto);
        Assert.DoesNotContain("Por vencer", texto);
    }

    [Fact]
    public async Task La_pantalla_de_usuarios_lista_a_las_personas_con_su_correo_efectivo()
    {
        var (estado, _, texto) = await PedirAsync("admin", "/administracion/usuarios");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("DEV\\autor-cal", texto);
        Assert.Contains("autor-cal@planta.local", texto); // convención de appsettings.Development.json
    }

    [Fact]
    public async Task La_pantalla_de_correo_avisa_que_esta_desactivado_en_lugar_de_mostrar_una_cola_vacia_enganosa()
    {
        var (estado, _, texto) = await PedirAsync("admin", "/administracion/correo");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("El correo está desactivado", texto);
    }

    [Fact]
    public async Task El_panel_de_administracion_ofrece_los_accesos_a_usuarios_y_a_la_cola_de_correo()
    {
        var html = await HtmlAsync("admin", "/administracion");

        Assert.Contains("href=\"administracion/usuarios\"", html);
        Assert.Contains("href=\"administracion/correo\"", html);
        Assert.Contains("Usuarios y roles", Texto(html));
        Assert.Contains("Cola de correo", Texto(html));
    }

    [Theory]
    [InlineData("autor-cal")]
    [InlineData("admin")]
    [InlineData("auditor")]
    [InlineData("revisor")]    // tiene una etapa asignada en este documento
    [InlineData("aprobador")]
    public async Task El_detalle_del_documento_se_muestra_a_gestores_y_asignados(string persona)
    {
        var (estado, _, texto) = await PedirAsync(persona, $"/documentos/{app.DocumentoId}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains(app.CodigoDocumento, texto);
        Assert.Contains("Procedimiento de arranque", texto);
        Assert.Contains("En revisión", texto);
    }

    [Fact]
    public async Task Un_lector_no_puede_abrir_el_detalle_de_un_documento_en_borrador_o_flujo()
    {
        var (estado, redireccion, _) = await PedirAsync("lector", $"/documentos/{app.DocumentoId}");

        Assert.Equal(HttpStatusCode.Redirect, estado);
        Assert.Contains("/acceso-denegado", redireccion);
    }

    // ---------- Lo que muestra el panel según quién mira ----------

    [Fact]
    public async Task El_revisor_asignado_ve_su_decision_y_en_su_bandeja_aparece_el_documento()
    {
        var (_, _, detalle) = await PedirAsync("revisor", $"/documentos/{app.DocumentoId}");
        var (_, _, bandeja) = await PedirAsync("revisor", "/pendientes");

        Assert.Contains("Tu decisión", detalle);
        Assert.Contains("Aprobar", detalle);
        Assert.Contains("Rechazar", detalle);
        Assert.Contains(app.CodigoDocumento, bandeja);
    }

    [Fact]
    public async Task El_aprobador_no_tiene_tarea_donde_aun_no_es_su_turno_pero_si_donde_ya_lo_es()
    {
        var (_, _, detalle) = await PedirAsync("aprobador", $"/documentos/{app.DocumentoId}");   // espera al revisor
        var (_, _, bandeja) = await PedirAsync("aprobador", "/pendientes");

        Assert.DoesNotContain("Tu decisión", detalle);
        Assert.DoesNotContain(app.CodigoDocumento, bandeja);
        Assert.Contains(app.CodigoEnAprobacion, bandeja);                                     // el revisor ya aprobó: le toca
    }

    [Fact]
    public async Task El_autor_ve_el_flujo_y_la_opcion_de_cancelarlo_pero_no_decide()
    {
        var (_, _, t) = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoId}");

        Assert.Contains("Flujo de aprobación", t);
        Assert.Contains("Revisión técnica", t);
        Assert.Contains("Cancelar el flujo", t);
        Assert.DoesNotContain("Tu decisión", t);
        Assert.DoesNotContain("Enviar a revisión", t); // ya está en flujo: no es editable
    }

    [Fact]
    public async Task La_fuente_solo_se_ofrece_a_gestores()
    {
        // Los enlaces viven en atributos href, así que aquí se mira el HTML sin quitarle las etiquetas.
        var autor = await HtmlAsync("autor-cal", $"/documentos/{app.DocumentoId}");
        var revisor = await HtmlAsync("revisor", $"/documentos/{app.DocumentoId}");

        Assert.Contains($"archivos/{app.RevisionId}/fuente", autor);
        Assert.DoesNotContain($"archivos/{app.RevisionId}/fuente", revisor);
        Assert.Contains($"archivos/{app.RevisionId}/pdf", revisor);
    }

    [Fact]
    public async Task El_menu_muestra_a_cada_rol_solo_lo_que_le_corresponde()
    {
        var (_, _, revisor) = await PedirAsync("revisor", "/mi-acceso");
        var (_, _, admin) = await PedirAsync("admin", "/mi-acceso");
        var (_, _, lector) = await PedirAsync("lector", "/mi-acceso");

        Assert.Contains("Mis tareas", revisor);
        Assert.DoesNotContain("Administración", revisor);
        Assert.Contains("Administración", admin);
        Assert.Contains("Documentos", admin);
        Assert.DoesNotContain("Mis tareas", lector);
        Assert.DoesNotContain("Documentos Mi acceso", lector);
    }

    // ---------- Publicación, nuevas revisiones y retiro ----------

    [Fact]
    public async Task Un_documento_publicado_muestra_su_vigencia_y_ofrece_nueva_revision_al_autor()
    {
        var (estado, _, autor) = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Vigente", autor);
        Assert.Contains("Vigente desde", autor);
        Assert.Contains("Próxima revisión periódica", autor);
        Assert.Contains("Nueva revisión", autor);
        Assert.DoesNotContain("Retirar documento", autor); // retirar es solo del Administrador
    }

    [Fact]
    public async Task Solo_el_Administrador_ve_la_opcion_de_retirar()
    {
        var (_, _, admin) = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}");
        var (_, _, auditor) = await PedirAsync("auditor", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Contains("Retirar documento", admin);
        Assert.DoesNotContain("Retirar documento", auditor);
        Assert.DoesNotContain("Nueva revisión", auditor); // el Auditor solo consulta
    }

    [Fact]
    public async Task Con_una_revision_en_curso_no_se_ofrece_abrir_otra()
    {
        var (_, _, autor) = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoId}");

        Assert.DoesNotContain("Nueva revisión…", autor);
    }

    [Fact]
    public async Task La_ultima_etapa_ofrece_fecha_de_vigencia_y_la_intermedia_no()
    {
        var (_, _, ultima) = await PedirAsync("aprobador", $"/documentos/{app.DocumentoEnAprobacionId}");
        var (_, _, intermedia) = await PedirAsync("revisor", $"/documentos/{app.DocumentoId}");

        Assert.Contains("Tu decisión", ultima);
        Assert.Contains("Entra en vigor a partir de", ultima);
        Assert.Contains("última etapa", ultima);
        Assert.Contains("Tu decisión", intermedia);
        Assert.DoesNotContain("Entra en vigor a partir de", intermedia);
    }

    [Fact]
    public async Task El_PDF_vigente_es_de_lectura_abierta_pero_el_borrador_no()
    {
        using var lector = await app.ClienteComoAsync("lector");

        using var vigente = await lector.GetAsync($"/archivos/{app.RevisionPublicadaId}/pdf");
        using var enFlujo = await lector.GetAsync($"/archivos/{app.RevisionId}/pdf");

        Assert.Equal(HttpStatusCode.OK, vigente.StatusCode);
        Assert.Equal(app.ContenidoPdfVigente, await vigente.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Forbidden, enFlujo.StatusCode);
    }

    [Fact]
    public async Task La_pantalla_de_areas_ofrece_designar_al_dueno()
    {
        var (_, _, t) = await PedirAsync("admin", "/administracion/areas");

        Assert.Contains("Calidad", t);
        // El panel de gestión del área se abre con un clic; aquí se comprueba que la pantalla carga con el área sembrada.
        Assert.Contains("Gestionar", t);
    }

    // ---------- Aprobación pendiente de entrar en vigor ----------

    [Fact]
    public async Task El_Administrador_ve_como_cambiar_la_fecha_o_anular_una_aprobacion_pendiente()
    {
        var (estado, _, t) = await PedirAsync("admin", $"/documentos/{app.DocumentoProgramadoId}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Entrada en vigor pendiente", t);
        Assert.Contains("Cambiar fecha", t);
        Assert.Contains("Anular la aprobación y devolver a borrador", t);
        Assert.Contains("Entrará en vigor", t);
    }

    [Theory]
    [InlineData("autor-cal")]
    [InlineData("auditor")]
    [InlineData("aprobador")]
    public async Task Nadie_mas_que_el_Administrador_ve_esas_acciones(string persona)
    {
        var (estado, _, t) = await PedirAsync(persona, $"/documentos/{app.DocumentoProgramadoId}");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("Entrará en vigor", t);                        // sí ven que está programada
        Assert.DoesNotContain("Entrada en vigor pendiente", t);          // pero no pueden intervenirla
        Assert.DoesNotContain("Anular la aprobación", t);
    }

    [Fact]
    public async Task Esas_acciones_no_aparecen_en_una_revision_vigente_ni_en_una_en_flujo()
    {
        var (_, _, vigente) = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}");
        var (_, _, enFlujo) = await PedirAsync("admin", $"/documentos/{app.DocumentoId}");

        Assert.DoesNotContain("Anular la aprobación", vigente);
        Assert.DoesNotContain("Anular la aprobación", enFlujo);
    }

    [Fact]
    public async Task Una_revision_aprobada_pero_programada_no_es_visible_para_un_lector()
    {
        var (_, _, lista, _) = (await PedirConsultaAsync("lector", "/lista-maestra"));
        var (_, _, buscar, _) = (await PedirConsultaAsync("lector", "/buscar?q=programado"));
        var (_, _, visor, _) = (await PedirConsultaAsync("lector", $"/doc/{app.CodigoProgramado}"));

        Assert.DoesNotContain(app.CodigoProgramado, lista);
        Assert.Contains("No se encontró ningún documento", buscar);
        Assert.Contains("Documento no disponible", visor);
        Assert.DoesNotContain(AplicacionWebFixture.TituloProgramado, lista + buscar + visor);
    }

    private async Task<(HttpStatusCode Estado, string Html, string Texto, string? Ubicacion)> PedirConsultaAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        var html = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, html, Texto(html), r.Headers.Location?.ToString());
    }

    // ---------- Descarga de archivos ----------

    [Fact]
    public async Task El_autor_descarga_el_PDF_en_linea_con_cabeceras_seguras_y_contenido_intacto()
    {
        using var cliente = await app.ClienteComoAsync("autor-cal");

        using var r = await cliente.GetAsync($"/archivos/{app.RevisionId}/pdf");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/pdf", r.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline", r.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains($"{app.CodigoDocumento}_Rev.00.pdf", r.Content.Headers.ContentDisposition.ToString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(app.ContenidoPdf, await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task La_fuente_se_descarga_como_adjunto()
    {
        using var cliente = await app.ClienteComoAsync("autor-cal");

        using var r = await cliente.GetAsync($"/archivos/{app.RevisionId}/fuente");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith(".docx", r.Content.Headers.ContentDisposition.FileName!.Trim('"'));
    }

    [Theory]
    [InlineData("revisor", "pdf", HttpStatusCode.OK)]       // etapa asignada
    [InlineData("aprobador", "pdf", HttpStatusCode.OK)]
    [InlineData("auditor", "pdf", HttpStatusCode.OK)]
    [InlineData("admin", "fuente", HttpStatusCode.OK)]
    [InlineData("auditor", "fuente", HttpStatusCode.OK)]
    [InlineData("lector", "pdf", HttpStatusCode.Forbidden)] // borrador/en flujo: no es lectura abierta
    [InlineData("revisor", "fuente", HttpStatusCode.Forbidden)]
    [InlineData("lector", "fuente", HttpStatusCode.Forbidden)]
    public async Task La_descarga_aplica_la_matriz_de_permisos(string persona, string tipo, HttpStatusCode esperado)
    {
        using var cliente = await app.ClienteComoAsync(persona);

        using var r = await cliente.GetAsync($"/archivos/{app.RevisionId}/{tipo}");

        Assert.Equal(esperado, r.StatusCode);
        if (esperado == HttpStatusCode.Forbidden)
        {
            Assert.Contains("permiso", await r.Content.ReadAsStringAsync()); // con cuerpo, no una página vacía
        }
    }

    [Theory]
    [InlineData("/archivos/999999/pdf")]
    [InlineData("/archivos/1/otracosa")]
    public async Task Pedir_algo_inexistente_o_con_tipo_invalido_da_404_con_mensaje(string ruta)
    {
        using var cliente = await app.ClienteComoAsync("admin");

        using var r = await cliente.GetAsync(ruta);

        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.NotEmpty(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Una_ruta_con_recorrido_de_directorios_no_llega_a_ningun_archivo()
    {
        using var cliente = await app.ClienteComoAsync("admin");

        foreach (var ruta in new[] { "/archivos/..%2f..%2fappsettings.json/pdf", "/archivos/1/..%2f..%2fapp", "/archivos/%2e%2e/pdf" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Fact]
    public async Task Los_archivos_subidos_no_son_accesibles_como_contenido_estatico()
    {
        using var cliente = await app.ClienteComoAsync("admin");

        foreach (var ruta in new[] { "/App_Data/archivos", "/archivos/" + DateTime.UtcNow.Year, "/appsettings.json" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        }
    }
}
