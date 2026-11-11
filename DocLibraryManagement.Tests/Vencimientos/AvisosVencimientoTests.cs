using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Vencimientos;

/// <summary>Los avisos a 30 y 7 días, el «vencido», el escalamiento y la vigencia próxima, con el reloj simulado.</summary>
public class AvisosVencimientoTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static List<Notificacion> De(List<Notificacion> todas, string tipo) => [.. todas.Where(n => n.Tipo == tipo)];

    /// <summary>Documento publicado, con el área y el reloj listos. Si se pide, con un dueño de área.</summary>
    private async Task<(Escenario E, EscenarioFlujo F)> VigenteAsync(bool conDueno = true, bool correo = true)
    {
        var e = new Escenario(bd, correo: correo);
        var f = await e.PrepararFlujoAsync();
        if (conDueno)
        {
            await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        }

        await e.PublicarPrimeraAsync(f);
        return (e, f);
    }

    [Fact]
    public async Task Con_el_correo_desactivado_el_proceso_no_encola_nada()
    {
        var (e, f) = await VigenteAsync(correo: false);
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -10);

        var resumen = await e.Vencimientos.ProcesarAsync();

        Assert.Equal(0, resumen.AvisosEncolados);
        Assert.Empty(await e.NotificacionesDeAsync(f.Documento.Codigo));
    }

    [Fact]
    public async Task Lejos_del_vencimiento_no_se_avisa()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 31);

        await e.Vencimientos.ProcesarAsync();

        Assert.Empty(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AlertaVencimiento));
    }

    [Fact]
    public async Task A_los_30_dias_avisa_al_responsable_una_sola_vez_aunque_el_proceso_corra_muchas_veces()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 30);

        await e.Vencimientos.ProcesarAsync();
        e.Reloj.Advance(TimeSpan.FromHours(5));
        await e.Vencimientos.ProcesarAsync();
        e.Reloj.Advance(TimeSpan.FromDays(10));
        await e.Vencimientos.ProcesarAsync();

        var alerta = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AlertaVencimiento));
        Assert.Equal(await e.IdDeAsync(f.Autor), alerta.DestinatarioId); // el responsable es quien creó el documento
        Assert.Contains(f.Documento.Codigo, alerta.Asunto);
        Assert.Contains("30 día(s)", alerta.Asunto);
        Assert.Contains("Revisado sin cambios", alerta.Cuerpo);
        Assert.Contains($"https://docs.test/documentos/{f.Documento.Id}", alerta.Cuerpo);
    }

    [Fact]
    public async Task A_los_7_dias_sale_un_segundo_aviso_distinto()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 30);
        await e.Vencimientos.ProcesarAsync();

        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 7);
        await e.Vencimientos.ProcesarAsync();
        await e.Vencimientos.ProcesarAsync();

        var alertas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AlertaVencimiento);
        Assert.Equal(2, alertas.Count);
        Assert.Contains("30 día(s)", alertas[0].Asunto);
        Assert.Contains("7 día(s)", alertas[1].Asunto);
    }

    [Fact]
    public async Task Si_la_primera_vez_que_se_nota_faltan_menos_de_7_dias_solo_sale_el_urgente()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 5); // el sistema estuvo apagado o se activó el correo tarde

        await e.Vencimientos.ProcesarAsync();

        var alerta = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AlertaVencimiento));
        Assert.Contains("5 día(s)", alerta.Asunto);
    }

    [Fact]
    public async Task Al_vencer_avisa_al_responsable_y_escala_al_dueno_del_area_sin_dejar_de_estar_vigente()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -2);

        await e.Vencimientos.ProcesarAsync();
        e.Reloj.Advance(TimeSpan.FromDays(1));
        await e.Vencimientos.ProcesarAsync(); // un día después: sin duplicados

        var todas = await e.NotificacionesDeAsync(f.Documento.Codigo);
        var vencido = Assert.Single(De(todas, EventosCorreo.DocumentoVencido));
        Assert.Equal(await e.IdDeAsync(f.Autor), vencido.DestinatarioId);
        Assert.Contains("sigue vigente", vencido.Cuerpo);
        var escalamiento = Assert.Single(De(todas, EventosCorreo.EscalamientoVencido));
        Assert.Equal(f.AprobadorId, escalamiento.DestinatarioId); // el dueño del área
        Assert.Contains("su responsable (Usuario ", escalamiento.Cuerpo); // dice a quién se está esperando
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId)); // el documento NO se retira ni se bloquea
    }

    [Fact]
    public async Task Sin_dueno_de_area_se_escala_a_los_administradores()
    {
        var (e, f) = await VigenteAsync(conDueno: false);
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -1);

        await e.Vencimientos.ProcesarAsync();

        var escalamientos = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.EscalamientoVencido);
        var idAdmin = await e.IdDeAsync(f.Admin);
        Assert.Contains(escalamientos, n => n.DestinatarioId == idAdmin);
        Assert.DoesNotContain(escalamientos, n => n.DestinatarioId == f.AprobadorId); // un Aprobador cualquiera no es dueño
    }

    [Fact]
    public async Task Si_el_responsable_es_tambien_el_dueno_la_escalacion_va_a_los_administradores_y_no_se_duplica()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var duenoResponsable = await e.NuevoActorAsync([RolSistema.Autor, RolSistema.Aprobador], f.CodigoArea);
        var idDueno = await e.IdDeAsync(duenoResponsable);
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, idDueno);
        var doc = await e.Documentos.CrearAsync(duenoResponsable, new("Propio", f.AreaId, null, f.TipoId));
        var revId = doc.Revisiones.Single().Id;
        await e.SubirPdfAsync(duenoResponsable, revId);
        await e.Flujo.IniciarAsync(duenoResponsable, revId, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        await e.AprobarRevisionAsync(f, revId);
        await e.IrASiguienteRevisionAsync(f, revId, -1);

        await e.Vencimientos.ProcesarAsync();

        var todas = await e.NotificacionesDeAsync(doc.Codigo);
        Assert.Equal(idDueno, Assert.Single(De(todas, EventosCorreo.DocumentoVencido)).DestinatarioId);
        var escalamientos = De(todas, EventosCorreo.EscalamientoVencido);
        Assert.DoesNotContain(escalamientos, n => n.DestinatarioId == idDueno); // no se avisa dos veces a la misma persona
    }

    [Fact]
    public async Task Al_extenderse_la_fecha_el_ciclo_siguiente_vuelve_a_avisar()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 20);
        await e.Vencimientos.ProcesarAsync();
        var solicitud = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, solicitud, true, null);

        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 30); // ya con la fecha nueva
        await e.Vencimientos.ProcesarAsync();

        var alertas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AlertaVencimiento);
        Assert.Equal(2, alertas.Count); // la del ciclo anterior y la del nuevo
    }

    [Fact]
    public async Task Una_revision_reemplazada_deja_de_generar_avisos()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id);   // la Rev.00 queda obsoleta
        var antes = (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count;
        await e.IrASiguienteRevisionAsync(f, nueva.Id, -3);

        await e.Vencimientos.ProcesarAsync();

        var avisos = (await e.NotificacionesDeAsync(f.Documento.Codigo)).Skip(antes).ToList();
        Assert.All(avisos, n => Assert.Contains("Rev.01", n.Asunto)); // solo la vigente de ahora
        Assert.NotEmpty(avisos);
    }

    [Fact]
    public async Task Un_documento_retirado_no_genera_avisos()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");
        var antes = (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -30);

        await e.Vencimientos.ProcesarAsync();

        Assert.Equal(antes, (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count);
    }

    // ---------- Vigencia próxima ----------

    [Fact]
    public async Task Una_revision_aprobada_que_entra_en_vigor_pronto_avisa_una_vez()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(2));

        await e.Vencimientos.ProcesarAsync();
        await e.Vencimientos.ProcesarAsync();

        var aviso = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.VigenciaProxima));
        Assert.Equal(await e.IdDeAsync(f.Autor), aviso.DestinatarioId);
        Assert.Contains("entra en vigor", aviso.Asunto);
    }

    [Fact]
    public async Task Si_falta_mucho_para_entrar_en_vigor_no_se_avisa_todavia()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(10));

        await e.Vencimientos.ProcesarAsync();

        Assert.Empty(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.VigenciaProxima));
    }

    [Fact]
    public async Task Si_se_reprograma_la_vigencia_el_aviso_se_repite_para_la_fecha_nueva()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(2));
        await e.Vencimientos.ProcesarAsync();

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(3), "Se pospone");
        await e.Vencimientos.ProcesarAsync();

        Assert.Equal(2, De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.VigenciaProxima).Count);
    }

    // ---------- Cola de punta a punta ----------

    [Fact]
    public async Task Los_avisos_de_vencimiento_se_entregan_por_la_cola_normal()
    {
        var (e, f) = await VigenteAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -1);
        await e.Vencimientos.ProcesarAsync();

        await e.ProcesarColaAsync();

        var todas = await e.NotificacionesDeAsync(f.Documento.Codigo);
        Assert.All(De(todas, EventosCorreo.DocumentoVencido).Concat(De(todas, EventosCorreo.EscalamientoVencido)),
            n => Assert.Equal(EstadoNotificacion.Enviado, n.Estado));
        Assert.Contains(e.CorreoSalida.Enviados, m => m.Asunto.Contains("Venció la revisión") && m.Asunto.Contains(f.Documento.Codigo));
    }
}
