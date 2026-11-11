using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// Reportes, bitácora, historial y exportaciones contra la aplicación real. Las pantallas son estáticas (se generan al pedirlas):
/// aquí se comprueba su contenido y sus permisos; los filtros viajan en la URL, así que también se prueban.
/// </summary>
public class WebReportesTests(AplicacionWebFixture app) : IClassFixture<AplicacionWebFixture>
{
    private const string TipoXlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static string Texto(string html) =>
        Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private async Task<(HttpStatusCode Estado, string? Redireccion, string Html, string Texto)> PedirAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        var html = await r.Content.ReadAsStringAsync();
        return (r.StatusCode, r.Headers.Location?.ToString(), html, Texto(html));
    }

    private async Task<(HttpStatusCode Estado, string? TipoContenido, byte[] Cuerpo)> DescargarAsync(string persona, string ruta)
    {
        using var cliente = await app.ClienteComoAsync(persona);
        using var r = await cliente.GetAsync(ruta);
        return (r.StatusCode, r.Content.Headers.ContentType?.ToString(), await r.Content.ReadAsByteArrayAsync());
    }

    private static string Csv(byte[] cuerpo) => new UTF8Encoding(false).GetString(cuerpo.AsSpan(3)); // sin BOM

    // ---------- Acceso a las pantallas ----------

    [Theory]
    [InlineData("admin", "/reportes", true)]
    [InlineData("auditor", "/reportes", true)]
    [InlineData("autor-cal", "/reportes", true)]
    [InlineData("aprobador", "/reportes", true)]
    [InlineData("revisor", "/reportes", false)]
    [InlineData("lector", "/reportes", false)]
    [InlineData("admin", "/reportes/vencimientos", true)]
    [InlineData("autor-cal", "/reportes/vencimientos", true)]
    [InlineData("aprobador", "/reportes/vencimientos", true)]
    [InlineData("revisor", "/reportes/vencimientos", false)]
    [InlineData("lector", "/reportes/vencimientos", false)]
    [InlineData("admin", "/reportes/tareas", true)]
    [InlineData("auditor", "/reportes/tareas", true)]
    [InlineData("revisor", "/reportes/tareas", false)]
    [InlineData("lector", "/reportes/tareas", false)]
    [InlineData("admin", "/auditoria", true)]
    [InlineData("auditor", "/auditoria", true)]
    [InlineData("autor-cal", "/auditoria", false)]
    [InlineData("aprobador", "/auditoria", false)]
    [InlineData("revisor", "/auditoria", false)]
    [InlineData("lector", "/auditoria", false)]
    public async Task Las_pantallas_de_reportes_respetan_el_rol(string persona, string ruta, bool permitido)
    {
        var (estado, redireccion, _, _) = await PedirAsync(persona, ruta);

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
    [InlineData("auditor", true)]
    [InlineData("autor-cal", true)]
    [InlineData("aprobador", true)]   // dueño del área
    [InlineData("revisor", true)]     // tuvo una etapa en el documento
    [InlineData("lector", false)]
    public async Task El_historial_de_un_documento_lo_ven_quienes_participan(string persona, bool permitido)
    {
        var (estado, redireccion, _, texto) = await PedirAsync(persona, $"/documentos/{app.DocumentoPublicadoId}/historial");

        if (permitido)
        {
            Assert.Equal(HttpStatusCode.OK, estado);
            Assert.Contains(app.CodigoPublicado, texto);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, estado);
            Assert.Contains("/acceso-denegado", redireccion);
        }
    }

    [Fact]
    public async Task Sin_sesion_todo_redirige_al_acceso()
    {
        using var cliente = app.CrearCliente();

        foreach (var ruta in new[] { "/reportes", "/reportes/vencimientos", "/reportes/tareas", "/auditoria", $"/documentos/{app.DocumentoId}/historial" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/dev/login", r.Headers.Location!.ToString());
        }
    }

    // ---------- Contenido ----------

    [Fact]
    public async Task El_indice_de_reportes_ofrece_la_bitacora_solo_a_administrador_y_auditor()
    {
        var admin = await PedirAsync("admin", "/reportes");
        var autor = await PedirAsync("autor-cal", "/reportes");

        Assert.Contains("href=\"auditoria\"", admin.Html);
        Assert.DoesNotContain("href=\"auditoria\"", autor.Html);
        Assert.Contains("href=\"reportes/vencimientos\"", autor.Html);
        Assert.Contains("href=\"reportes/tareas\"", autor.Html);
    }

    [Fact]
    public async Task El_menu_muestra_reportes_y_bitacora_segun_el_rol()
    {
        var auditor = await PedirAsync("auditor", "/");
        var autor = await PedirAsync("autor-cal", "/");
        var lector = await PedirAsync("lector", "/");

        Assert.Contains("href=\"reportes\"", auditor.Html);
        Assert.Contains("href=\"auditoria\"", auditor.Html);
        Assert.Contains("href=\"reportes\"", autor.Html);
        Assert.DoesNotContain("href=\"auditoria\"", autor.Html);
        Assert.DoesNotContain("href=\"reportes\"", lector.Html);
    }

    [Fact]
    public async Task Los_vencimientos_por_omision_muestran_solo_lo_que_requiere_atencion()
    {
        var (_, _, _, texto) = await PedirAsync("admin", "/reportes/vencimientos");

        Assert.Contains("No hay documentos que coincidan con este filtro.", texto); // los sembrados vencen dentro de un año
        Assert.DoesNotContain(app.CodigoPublicado, texto);
    }

    [Fact]
    public async Task Los_vencimientos_de_todos_los_vigentes_muestran_la_situacion_y_el_responsable()
    {
        var (_, _, _, texto) = await PedirAsync("admin", "/reportes/vencimientos?estado=3");

        Assert.Contains(app.CodigoPublicado, texto);
        Assert.Contains("Al día", texto);
        Assert.Contains("DEV\\autor-cal", texto);
        Assert.DoesNotContain(app.CodigoProgramado, texto);         // aprobado, todavía no vigente
    }

    [Fact]
    public async Task Los_vencimientos_se_filtran_por_texto_desde_la_url()
    {
        var (_, _, _, conTexto) = await PedirAsync("admin", $"/reportes/vencimientos?estado=3&texto={Uri.EscapeDataString(app.CodigoPublicado)}");
        var (_, _, _, sinCoincidencia) = await PedirAsync("admin", "/reportes/vencimientos?estado=3&texto=zzzzinexistente");

        Assert.Contains(app.CodigoPublicado, conTexto);
        Assert.DoesNotContain(app.CodigoPublicado, sinCoincidencia);
        Assert.Contains("No hay documentos que coincidan", sinCoincidencia);
    }

    [Fact]
    public async Task Un_filtro_de_texto_invalido_se_explica_en_pantalla_sin_error_del_servidor()
    {
        var (estado, _, _, texto) = await PedirAsync("admin", "/reportes/vencimientos?estado=3&texto=a");

        Assert.Equal(HttpStatusCode.OK, estado);
        Assert.Contains("al menos una palabra", texto);
    }

    [Fact]
    public async Task Un_estado_fuera_de_rango_se_trata_como_el_predeterminado()
    {
        var (estado, _, _, _) = await PedirAsync("admin", "/reportes/vencimientos?estado=99");

        Assert.Equal(HttpStatusCode.OK, estado);
    }

    [Fact]
    public async Task Las_tareas_pendientes_muestran_a_cada_persona_con_su_tarea()
    {
        var (_, _, _, texto) = await PedirAsync("admin", "/reportes/tareas");

        Assert.Contains("Resumen por persona", texto);
        Assert.Contains(app.CodigoDocumento, texto);                 // en revisión: espera al revisor
        Assert.Contains(app.CodigoEnAprobacion, texto);              // en aprobación: espera al aprobador
        Assert.Contains("DEV\\revisor", texto);
        Assert.Contains("DEV\\aprobador", texto);
        Assert.Contains("Revisión técnica", texto);
    }

    [Fact]
    public async Task Las_tareas_se_filtran_por_tipo()
    {
        var (_, _, _, soloAprobaciones) = await PedirAsync("admin", "/reportes/tareas?tipo=2");

        Assert.Contains(app.CodigoEnAprobacion, soloAprobaciones);
        Assert.DoesNotContain(app.CodigoDocumento, soloAprobaciones);
    }

    [Fact]
    public async Task El_historial_del_documento_publicado_muestra_sus_dos_revisiones_y_las_etapas()
    {
        var (_, _, _, texto) = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}/historial");

        Assert.Contains("Rev.00", texto);
        Assert.Contains("Rev.01", texto);
        Assert.Contains("Obsoleto", texto);
        Assert.Contains("Vigente", texto);
        Assert.Contains("Envío 1", texto);
        Assert.Contains("Revisión técnica", texto);
        Assert.Contains("Aprobada", texto);
        Assert.Contains("DEV\\revisor", texto);
    }

    [Fact]
    public async Task El_historial_enlaza_a_la_bitacora_solo_para_administrador_y_auditor()
    {
        var admin = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}/historial");
        var autor = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoPublicadoId}/historial");

        Assert.Contains("auditoria?entidad=Documento", admin.Html);
        Assert.DoesNotContain("auditoria?entidad=Documento", autor.Html);
    }

    [Fact]
    public async Task La_bitacora_muestra_los_eventos_y_se_filtra_desde_la_url()
    {
        var (_, _, _, todo) = await PedirAsync("auditor", $"/auditoria?identificador={Uri.EscapeDataString(app.CodigoPublicado)}");
        var (_, _, _, soloCrear) = await PedirAsync("auditor", $"/auditoria?identificador={Uri.EscapeDataString(app.CodigoPublicado)}&accion=Documento.Crear");

        Assert.Contains("Documento.Crear", todo);
        Assert.Contains("Revision.Publicar", todo);
        Assert.Contains("1 evento(s)", soloCrear);
        Assert.Contains("DEV\\autor-cal", soloCrear);
    }

    [Fact]
    public async Task La_bitacora_valida_las_fechas_y_los_textos_sin_romper_la_pagina()
    {
        var alReves = await PedirAsync("auditor", "/auditoria?desde=2026-11-10&hasta=2026-11-01");
        var largo = await PedirAsync("auditor", $"/auditoria?texto={new string('x', 250)}");

        Assert.Equal(HttpStatusCode.OK, alReves.Estado);
        Assert.Contains("La fecha final no puede ser anterior a la inicial.", alReves.Texto);
        Assert.Equal(HttpStatusCode.OK, largo.Estado);
        Assert.Contains("no puede pasar de", largo.Texto);
    }

    [Fact]
    public async Task La_bitacora_escapa_el_contenido_que_muestra()
    {
        // El filtro vuelve a pintarse en el formulario: un valor con marcado no debe interpretarse como HTML.
        var (_, _, html, _) = await PedirAsync("auditor", "/auditoria?usuario=%22%3E%3Cscript%3Ealert(1)%3C/script%3E");

        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    [Fact]
    public async Task El_detalle_del_documento_ofrece_el_historial_a_todos_y_el_cambio_de_responsable_solo_al_administrador()
    {
        var admin = await PedirAsync("admin", $"/documentos/{app.DocumentoPublicadoId}");
        var autor = await PedirAsync("autor-cal", $"/documentos/{app.DocumentoPublicadoId}");

        Assert.Contains("Historial completo", admin.Texto);
        Assert.Contains("Historial completo", autor.Texto);
        Assert.Contains("Cambiar responsable", admin.Texto);
        Assert.DoesNotContain("Cambiar responsable", autor.Texto);
    }

    // ---------- Exportaciones ----------

    [Fact]
    public async Task Los_vencimientos_se_exportan_a_csv_con_bom_y_a_excel()
    {
        var csv = await DescargarAsync("admin", "/reportes/vencimientos/exportar?formato=csv&estado=3");
        var xlsx = await DescargarAsync("admin", "/reportes/vencimientos/exportar?formato=xlsx&estado=3");

        Assert.Equal(HttpStatusCode.OK, csv.Estado);
        Assert.StartsWith("text/csv", csv.TipoContenido);
        Assert.Equal([0xEF, 0xBB, 0xBF], csv.Cuerpo[..3]);
        Assert.Contains(app.CodigoPublicado, Csv(csv.Cuerpo));
        Assert.Contains("Código,Título", Csv(csv.Cuerpo));

        Assert.Equal(HttpStatusCode.OK, xlsx.Estado);
        Assert.Equal(TipoXlsx, xlsx.TipoContenido);
        Assert.Equal((byte)'P', xlsx.Cuerpo[0]);                     // un .xlsx es un ZIP: empieza con «PK»
        Assert.Equal((byte)'K', xlsx.Cuerpo[1]);
    }

    [Fact]
    public async Task El_formato_por_omision_es_excel_y_uno_invalido_se_rechaza()
    {
        var porOmision = await DescargarAsync("admin", "/reportes/vencimientos/exportar?estado=3");
        var invalido = await DescargarAsync("admin", "/reportes/vencimientos/exportar?formato=pdf");

        Assert.Equal(TipoXlsx, porOmision.TipoContenido);
        Assert.Equal(HttpStatusCode.BadRequest, invalido.Estado);
        Assert.Contains("Formato no válido", Encoding.UTF8.GetString(invalido.Cuerpo));
    }

    [Fact]
    public async Task Un_filtro_invalido_en_la_exportacion_da_400_con_el_motivo()
    {
        var estadoMalo = await DescargarAsync("admin", "/reportes/vencimientos/exportar?formato=csv&estado=99");
        var textoMalo = await DescargarAsync("admin", "/reportes/vencimientos/exportar?formato=csv&texto=a");
        var tipoMalo = await DescargarAsync("admin", "/reportes/tareas/exportar?formato=csv&tipo=99");
        var fechasAlReves = await DescargarAsync("auditor", "/auditoria/exportar?formato=csv&desde=2026-11-10&hasta=2026-11-01");

        foreach (var r in new[] { estadoMalo, textoMalo, tipoMalo, fechasAlReves })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Estado);
            Assert.False(string.IsNullOrWhiteSpace(Encoding.UTF8.GetString(r.Cuerpo)));
        }
    }

    [Fact]
    public async Task Las_tareas_se_exportan()
    {
        var csv = await DescargarAsync("admin", "/reportes/tareas/exportar?formato=csv");

        Assert.Equal(HttpStatusCode.OK, csv.Estado);
        Assert.Contains(app.CodigoDocumento, Csv(csv.Cuerpo));
        Assert.Contains("Días esperando", Csv(csv.Cuerpo));
    }

    [Fact]
    public async Task La_bitacora_se_exporta_con_los_filtros_de_la_pantalla()
    {
        var csv = await DescargarAsync("auditor", $"/auditoria/exportar?formato=csv&identificador={Uri.EscapeDataString(app.CodigoPublicado)}&accion=Documento.Crear");

        Assert.Equal(HttpStatusCode.OK, csv.Estado);
        var texto = Csv(csv.Cuerpo);
        Assert.Contains("Documento.Crear", texto);
        Assert.DoesNotContain("Revision.Publicar", texto);           // respeta el filtro de acción
        Assert.Equal(2, texto.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length); // encabezado + un evento
    }

    [Fact]
    public async Task El_historial_del_documento_se_exporta()
    {
        var csv = await DescargarAsync("admin", $"/documentos/{app.DocumentoPublicadoId}/historial/exportar?formato=csv");

        Assert.Equal(HttpStatusCode.OK, csv.Estado);
        Assert.Contains("Revisión técnica", Csv(csv.Cuerpo));
        Assert.Contains("Rev.01", Csv(csv.Cuerpo));
    }

    [Theory]
    [InlineData("lector", "/reportes/vencimientos/exportar?formato=csv")]
    [InlineData("revisor", "/reportes/vencimientos/exportar?formato=csv")]
    [InlineData("lector", "/reportes/tareas/exportar?formato=csv")]
    [InlineData("autor-cal", "/auditoria/exportar?formato=csv")]
    [InlineData("aprobador", "/auditoria/exportar?formato=csv")]
    [InlineData("lector", "/auditoria/exportar?formato=csv")]
    public async Task Pedir_la_exportacion_a_mano_no_da_mas_de_lo_que_la_pantalla_permite(string persona, string ruta)
    {
        var r = await DescargarAsync(persona, ruta);

        Assert.Equal(HttpStatusCode.Forbidden, r.Estado);
        Assert.StartsWith("text/plain", r.TipoContenido);
    }

    [Fact]
    public async Task La_exportacion_del_historial_exige_poder_ver_el_documento()
    {
        var lector = await DescargarAsync("lector", $"/documentos/{app.DocumentoPublicadoId}/historial/exportar?formato=csv");

        Assert.Equal(HttpStatusCode.Forbidden, lector.Estado);
    }

    [Fact]
    public async Task Un_documento_inexistente_en_el_historial_da_400_no_500()
    {
        var r = await DescargarAsync("admin", "/documentos/999999/historial/exportar?formato=csv");

        Assert.Equal(HttpStatusCode.BadRequest, r.Estado);
    }

    [Fact]
    public async Task Las_exportaciones_sin_sesion_redirigen_al_acceso()
    {
        using var cliente = app.CrearCliente();

        foreach (var ruta in new[] { "/reportes/vencimientos/exportar", "/reportes/tareas/exportar", "/auditoria/exportar", $"/documentos/{app.DocumentoId}/historial/exportar" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
    }

    [Fact]
    public async Task Las_descargas_no_dejan_que_el_navegador_adivine_el_tipo()
    {
        using var cliente = await app.ClienteComoAsync("admin");
        using var r = await cliente.GetAsync("/reportes/vencimientos/exportar?formato=csv&estado=3");

        Assert.Contains("nosniff", r.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains(".csv", r.Content.Headers.ContentDisposition?.FileName ?? "");
    }
}
