using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Vencimientos;

/// <summary>«Revisado sin cambios»: lo propone el responsable y lo decide el dueño del área en una sola etapa.</summary>
public class SinCambiosTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private const string Texto = "Revisé el procedimiento contra la práctica actual y sigue vigente tal cual.";

    /// <summary>Documento publicado cuyo dueño de área es <c>f.Aprobador</c>, con el reloj a «faltan N días» de su revisión periódica.</summary>
    private async Task<(Escenario E, EscenarioFlujo F)> ListoAsync(double dias = 20, bool conDueno = true)
    {
        var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        if (conDueno)
        {
            await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        }

        await e.PublicarPrimeraAsync(f);
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, dias);
        return (e, f);
    }

    private static async Task<DateTime> ProximaAsync(Escenario e, EscenarioFlujo f) =>
        (await e.Revisiones.ObtenerAsync(f.Admin, f.RevisionId)).FechaProximaRevisionUtc!.Value;

    private static List<Notificacion> De(List<Notificacion> todas, string tipo) => [.. todas.Where(n => n.Tipo == tipo)];

    // ---------- Proponer ----------

    [Fact]
    public async Task El_responsable_propone_dentro_de_la_ventana_y_queda_pendiente_sin_tocar_la_fecha()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var fechaAntes = await ProximaAsync(e, f);

        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var s = Assert.Single(await e.Vencimientos.HistorialAsync(f.Autor, f.RevisionId));
        Assert.Equal(id, s.Id);
        Assert.Equal(EstadoSolicitudSinCambios.Pendiente, s.Estado);
        Assert.Equal(Texto, s.Comentario);
        Assert.Equal(fechaAntes, s.FechaProximaAnteriorUtc);
        Assert.Equal(fechaAntes, await ProximaAsync(e, f));                              // proponer no extiende nada
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId));
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Revision.SinCambios.Proponer" && a.Comentario == Texto);
    }

    [Fact]
    public async Task Proponer_avisa_al_dueno_del_area_con_el_comentario()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;

        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var aviso = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.SinCambiosSolicitada));
        Assert.Equal(f.AprobadorId, aviso.DestinatarioId);
        Assert.Contains(Texto, aviso.Cuerpo);
        Assert.Contains($"https://docs.test/documentos/{f.Documento.Id}", aviso.Cuerpo);
    }

    [Fact]
    public async Task Sin_dueno_de_area_el_aviso_va_a_los_administradores()
    {
        var (e, f) = await ListoAsync(conDueno: false);
        using var _ = e;

        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var avisos = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.SinCambiosSolicitada);
        var idAdmin = await e.IdDeAsync(f.Admin);
        Assert.Contains(avisos, n => n.DestinatarioId == idAdmin);
        Assert.DoesNotContain(avisos, n => n.DestinatarioId == f.AprobadorId);
    }

    [Fact]
    public async Task Tambien_se_puede_proponer_ya_vencido()
    {
        var (e, f) = await ListoAsync(dias: -15);
        using var _ = e;

        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        Assert.True(id > 0);
    }

    [Fact]
    public async Task Con_meses_de_anticipacion_no_se_puede_proponer()
    {
        var (e, f) = await ListoAsync(dias: 200);
        using var _ = e;

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto));

        Assert.Contains("Todavía no toca revisar", ex.Message);
    }

    [Fact]
    public async Task Solo_el_responsable_o_un_administrador_puede_proponer()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var otroAutorMismaArea = await e.NuevoAutorAsync(f.CodigoArea);
        var autorOtraArea = await e.NuevoAutorAsync(Escenario.CodigoUnico("Z"));

        foreach (var actor in new[] { otroAutorMismaArea, autorOtraArea, f.Revisor, f.Aprobador, await e.NuevoActorAsync([RolSistema.Auditor]) })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.ProponerSinCambiosAsync(actor, f.RevisionId, Texto));
        }

        Assert.Empty(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId));
        Assert.True(await e.Vencimientos.ProponerSinCambiosAsync(f.Admin, f.RevisionId, Texto) > 0); // el administrador sí
    }

    [Fact]
    public async Task Un_responsable_que_perdio_el_acceso_al_area_ya_no_puede_proponer()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var sinArea = f.Autor with { Areas = new HashSet<string>() }; // sus grupos de AD cambiaron

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.ProponerSinCambiosAsync(sinArea, f.RevisionId, Texto));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task El_comentario_es_obligatorio(string comentario)
    {
        var (e, f) = await ListoAsync();
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, comentario));
    }

    [Fact]
    public async Task El_comentario_tiene_un_maximo()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, new string('x', ReglasVencimiento.MaximoComentario + 1)));
        Assert.True(await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, new string('x', ReglasVencimiento.MaximoComentario)) > 0);
    }

    [Fact]
    public async Task Solo_se_propone_sobre_la_revision_vigente()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Autor, borrador.Id, Texto));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Autor, int.MaxValue, Texto));
    }

    [Fact]
    public async Task No_puede_haber_dos_solicitudes_pendientes_de_la_misma_revision()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Admin, f.RevisionId, Texto));

        Assert.Contains("pendiente", ex.Message);
    }

    [Fact]
    public async Task Dos_propuestas_simultaneas_dejan_pasar_solo_una()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;

        var resultados = await Task.WhenAll(new[] { f.Autor, f.Admin }.Select(actor =>
            Intento.DeAsync(() => e.Vencimientos.ProponerSinCambiosAsync(actor, f.RevisionId, Texto))));

        Assert.Single(resultados, r => r.Exito);
        Assert.All(resultados.Where(r => !r.Exito), r => Assert.IsType<ReglaNegocioException>(r.Error));
        Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId));
    }

    [Fact]
    public async Task La_base_de_datos_rechaza_dos_pendientes_aunque_se_salte_el_servicio()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await using var db = e.Db();
        db.SolicitudesSinCambios.Add(new SolicitudSinCambios
        {
            RevisionId = f.RevisionId,
            SolicitanteId = f.AprobadorId,
            Comentario = "directo a la BD",
            FechaSolicitudUtc = DateTime.UtcNow,
            FechaProximaAnteriorUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // ---------- Decidir ----------

    [Fact]
    public async Task Al_aprobar_se_extiende_un_periodo_completo_desde_la_fecha_vigente_y_el_documento_sigue_vigente()
    {
        var (e, f) = await ListoAsync(dias: 20);
        using var _ = e;
        var antes = await ProximaAsync(e, f);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, "De acuerdo");

        var despues = await ProximaAsync(e, f);
        Assert.Equal(antes.AddMonths(12), despues);
        Assert.Equal(EstadoVencimiento.AlDia, ReglasVencimiento.Clasificar(despues, e.Reloj.GetUtcNow().UtcDateTime));
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId));

        var s = Assert.Single(await e.Vencimientos.HistorialAsync(f.Autor, f.RevisionId));
        Assert.Equal(EstadoSolicitudSinCambios.Aprobada, s.Estado);
        Assert.Equal(despues, s.FechaProximaNuevaUtc);
        Assert.Equal("De acuerdo", s.ComentarioDecision);
        Assert.NotNull(s.DecididaPor);
        Assert.NotNull(s.FechaDecisionUtc);

        var evento = Assert.Single(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Revision.SinCambios.Aprobar");
        Assert.Equal(f.Aprobador.Identificador, evento.Usuario);
        Assert.Contains($"{antes:yyyy-MM-dd}", evento.ValorAnterior);
        Assert.Contains($"{despues:yyyy-MM-dd}", evento.ValorNuevo);
        Assert.Contains(Texto, evento.Comentario);
    }

    [Fact]
    public async Task Si_ya_vencio_la_extension_cuenta_desde_hoy()
    {
        var (e, f) = await ListoAsync(dias: -60);
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);

        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime.AddMonths(12), await ProximaAsync(e, f));
    }

    [Fact]
    public async Task Aprobar_avisa_al_solicitante_con_la_fecha_nueva()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, "Adelante");

        var aviso = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.SinCambiosResuelta));
        Assert.Equal(await e.IdDeAsync(f.Autor), aviso.DestinatarioId);
        Assert.Contains("aprobó", aviso.Asunto);
        Assert.Contains("Adelante", aviso.Cuerpo);
        Assert.Contains((await ProximaAsync(e, f)).ToString("dd/MM/yyyy"), aviso.Cuerpo);
    }

    [Fact]
    public async Task Rechazar_exige_motivo_no_cambia_la_fecha_y_avisa_que_hay_que_abrir_una_revision()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var antes = await ProximaAsync(e, f);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, false, "  "));
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, false, "Cambió el equipo; hay que actualizarlo");

        Assert.Equal(antes, await ProximaAsync(e, f));
        Assert.Equal(EstadoSolicitudSinCambios.Rechazada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Autor, f.RevisionId)).Estado);
        Assert.Single(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Revision.SinCambios.Rechazar");
        var aviso = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.SinCambiosResuelta));
        Assert.Contains("rechazó", aviso.Asunto);
        Assert.Contains("Cambió el equipo", aviso.Cuerpo);
        Assert.Contains("abre una revisión nueva", aviso.Cuerpo);
    }

    [Fact]
    public async Task Tras_un_rechazo_se_puede_volver_a_proponer()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, false, "Falta evidencia");

        var otra = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Ahora con evidencia adjunta en la carpeta");

        Assert.NotEqual(id, otra);
        Assert.Equal(2, (await e.Vencimientos.HistorialAsync(f.Autor, f.RevisionId)).Count);
    }

    [Fact]
    public async Task Quien_propone_nunca_decide_su_propia_solicitud_ni_siendo_administrador()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Admin, f.RevisionId, Texto);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.DecidirSinCambiosAsync(f.Admin, id, true, null));

        Assert.Equal(EstadoSolicitudSinCambios.Pendiente, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId)).Estado);
    }

    [Fact]
    public async Task Un_dueno_que_es_el_responsable_no_decide_lo_que_el_mismo_propuso_y_lo_resuelve_un_administrador()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var duenoResponsable = await e.NuevoActorAsync([RolSistema.Autor, RolSistema.Aprobador], f.CodigoArea);
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, await e.IdDeAsync(duenoResponsable));
        var doc = await e.Documentos.CrearAsync(duenoResponsable, new("Propio", f.AreaId, null, f.TipoId));
        var revId = doc.Revisiones.Single().Id;
        await e.SubirPdfAsync(duenoResponsable, revId);
        await e.Flujo.IniciarAsync(duenoResponsable, revId, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        await e.AprobarRevisionAsync(f, revId);
        await e.IrASiguienteRevisionAsync(f, revId, 10);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(duenoResponsable, revId, Texto);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.DecidirSinCambiosAsync(duenoResponsable, id, true, null));
        await e.Vencimientos.DecidirSinCambiosAsync(f.Admin, id, true, null);

        Assert.Equal(EstadoSolicitudSinCambios.Aprobada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, revId)).Estado);
    }

    [Fact]
    public async Task Solo_decide_el_dueno_del_area_o_un_administrador()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        var otroAprobador = await e.NuevoAprobadorAsync();
        var duenoSinRol = f.Aprobador with { Roles = new HashSet<RolSistema> { RolSistema.Lector } }; // le quitaron el grupo de AD

        foreach (var actor in new[] { otroAprobador, f.Revisor, await e.NuevoAutorAsync(f.CodigoArea), duenoSinRol })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.DecidirSinCambiosAsync(actor, id, true, null));
        }

        Assert.Equal(EstadoSolicitudSinCambios.Pendiente, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId)).Estado);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Admin, id, true, null); // el administrador sí puede
    }

    [Fact]
    public async Task Una_solicitud_resuelta_no_se_decide_de_nuevo()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);
        var fecha = await ProximaAsync(e, f);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null));

        Assert.Equal(fecha, await ProximaAsync(e, f)); // no se extendió dos veces
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, int.MaxValue, true, null));
    }

    [Fact]
    public async Task Dos_decisiones_simultaneas_dejan_pasar_solo_una_y_la_fecha_se_extiende_una_vez()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var antes = await ProximaAsync(e, f);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var intentos = new (Actor Actor, bool Aprobar)[] { (f.Aprobador, true), (f.Admin, true), (f.Admin, false) };
        var resultados = await Task.WhenAll(intentos.Select(i =>
            Intento.DeAsync(() => e.Vencimientos.DecidirSinCambiosAsync(i.Actor, id, i.Aprobar, "x"))));

        Assert.Single(resultados, r => r.Exito);
        Assert.All(resultados.Where(r => !r.Exito), r => Assert.IsType<ReglaNegocioException>(r.Error));
        var s = Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId));
        var despues = await ProximaAsync(e, f);
        if (s.Estado == EstadoSolicitudSinCambios.Aprobada)
        {
            Assert.Equal(antes.AddMonths(12), despues); // exactamente una extensión
        }
        else
        {
            Assert.Equal(EstadoSolicitudSinCambios.Rechazada, s.Estado);
            Assert.Equal(antes, despues);
        }
    }

    // ---------- Cancelar ----------

    [Fact]
    public async Task Quien_propuso_puede_retirarla_y_volver_a_proponer()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await e.Vencimientos.CancelarSinCambiosAsync(f.Autor, id);

        Assert.Equal(EstadoSolicitudSinCambios.Cancelada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Autor, f.RevisionId)).Estado);
        Assert.Single(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Revision.SinCambios.Cancelar");
        Assert.True(await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto) > 0);
    }

    [Fact]
    public async Task Solo_quien_propuso_o_un_administrador_la_retira()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var otroAutor = await e.NuevoAutorAsync(f.CodigoArea);
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.CancelarSinCambiosAsync(f.Aprobador, id));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.CancelarSinCambiosAsync(otroAutor, id));
        await e.Vencimientos.CancelarSinCambiosAsync(f.Admin, id);

        Assert.Equal(EstadoSolicitudSinCambios.Cancelada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId)).Estado);
    }

    [Fact]
    public async Task No_se_puede_retirar_una_solicitud_ya_resuelta()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.CancelarSinCambiosAsync(f.Autor, id));
    }

    // ---------- Cierre automático ----------

    [Fact]
    public async Task Al_publicarse_una_revision_nueva_la_solicitud_pendiente_de_la_anterior_se_cancela()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);

        await e.AprobarRevisionAsync(f, nueva.Id);

        var s = Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId));
        Assert.Equal(EstadoSolicitudSinCambios.Cancelada, s.Estado);
        Assert.Contains("reemplazada", s.ComentarioDecision);
    }

    [Fact]
    public async Task Al_retirar_el_documento_la_solicitud_pendiente_se_cancela()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");

        var s = Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId));
        Assert.Equal(EstadoSolicitudSinCambios.Cancelada, s.Estado);
        Assert.Contains("retirado", s.ComentarioDecision);
    }

    [Fact]
    public async Task Decidir_una_solicitud_cuya_revision_dejo_de_ser_vigente_ya_no_aplica()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null)); // ya cancelada
    }

    // ---------- Consulta ----------

    [Fact]
    public async Task Las_pendientes_de_decision_se_ven_solo_el_dueno_y_los_administradores_y_nunca_las_propias()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        bool Esta(IReadOnlyList<SolicitudSinCambiosDto> l) => l.Any(s => s.CodigoDocumento == f.Documento.Codigo);

        Assert.True(Esta(await e.Vencimientos.PendientesDeDecisionAsync(f.Aprobador)));                       // el dueño
        Assert.True(Esta(await e.Vencimientos.PendientesDeDecisionAsync(f.Admin)));                           // un administrador
        Assert.False(Esta(await e.Vencimientos.PendientesDeDecisionAsync(await e.NuevoAprobadorAsync())));    // otro aprobador
        Assert.Empty(await e.Vencimientos.PendientesDeDecisionAsync(f.Autor));                                // un autor
        Assert.False(Esta(await e.Vencimientos.PendientesDeDecisionAsync(f.Revisor)));

        var dto = (await e.Vencimientos.PendientesDeDecisionAsync(f.Aprobador)).Single(s => s.CodigoDocumento == f.Documento.Codigo);
        Assert.Equal("Rev.00", dto.Etiqueta);
        Assert.Equal(Texto, dto.Comentario);
        Assert.Equal(f.Documento.Titulo, dto.Titulo);
    }

    [Fact]
    public async Task Lo_que_propone_un_administrador_no_aparece_en_su_propia_lista()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Admin, f.RevisionId, Texto);

        Assert.DoesNotContain(await e.Vencimientos.PendientesDeDecisionAsync(f.Admin), s => s.CodigoDocumento == f.Documento.Codigo);
        Assert.Contains(await e.Vencimientos.PendientesDeDecisionAsync(f.Aprobador), s => s.CodigoDocumento == f.Documento.Codigo);
    }

    [Fact]
    public async Task Una_vez_resuelta_ya_no_esta_entre_las_pendientes()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);

        Assert.DoesNotContain(await e.Vencimientos.PendientesDeDecisionAsync(f.Aprobador), s => s.CodigoDocumento == f.Documento.Codigo);
    }

    // ---------- Panel de la pantalla ----------

    [Fact]
    public async Task El_panel_refleja_lo_que_cada_persona_puede_hacer_antes_de_proponer()
    {
        var (e, f) = await ListoAsync(dias: 20);
        using var _ = e;

        var responsable = await e.Vencimientos.PanelAsync(f.Autor, f.RevisionId);
        var admin = await e.Vencimientos.PanelAsync(f.Admin, f.RevisionId);
        var dueno = await e.Vencimientos.PanelAsync(f.Aprobador, f.RevisionId);

        Assert.NotNull(responsable);
        Assert.Equal(EstadoVencimiento.PorVencer30, responsable.Vencimiento);
        Assert.Equal(20, responsable.DiasRestantes);
        Assert.Equal("Rev.00", responsable.Etiqueta);
        Assert.True(responsable.PuedeProponer);
        Assert.True(admin!.PuedeProponer);
        Assert.False(dueno!.PuedeProponer);                       // el dueño decide, no propone
        Assert.False(responsable.PuedeDecidir || responsable.PuedeCancelar);
        Assert.False(dueno.PuedeDecidir || dueno.PuedeCancelar);   // todavía no hay nada pendiente
        Assert.NotNull(responsable.NombreDueno);
    }

    [Fact]
    public async Task El_panel_con_una_solicitud_pendiente_habilita_decidir_al_dueno_y_retirar_a_quien_propuso()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        var responsable = await e.Vencimientos.PanelAsync(f.Autor, f.RevisionId);
        var dueno = await e.Vencimientos.PanelAsync(f.Aprobador, f.RevisionId);
        var admin = await e.Vencimientos.PanelAsync(f.Admin, f.RevisionId);

        Assert.NotNull(responsable!.Pendiente);
        Assert.False(responsable.PuedeProponer);                   // ya hay una pendiente
        Assert.False(responsable.PuedeDecidir);                    // no decide lo suyo
        Assert.True(responsable.PuedeCancelar);
        Assert.True(dueno!.PuedeDecidir);
        Assert.False(dueno.PuedeCancelar);                         // no es suya
        Assert.True(admin!.PuedeDecidir && admin.PuedeCancelar);
    }

    [Fact]
    public async Task El_panel_no_ofrece_proponer_lejos_del_vencimiento_ni_a_quien_no_participa()
    {
        var (e, f) = await ListoAsync(dias: 200);
        using var _ = e;

        Assert.False((await e.Vencimientos.PanelAsync(f.Autor, f.RevisionId))!.PuedeProponer);
        Assert.Equal(EstadoVencimiento.AlDia, (await e.Vencimientos.PanelAsync(f.Autor, f.RevisionId))!.Vencimiento);
        Assert.Null(await e.Vencimientos.PanelAsync(f.Revisor, f.RevisionId));                              // no participa: no hay panel
        Assert.Null(await e.Vencimientos.PanelAsync(await e.NuevoActorAsync([]), f.RevisionId));
    }

    [Fact]
    public async Task El_panel_de_una_revision_que_no_es_la_vigente_no_existe()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        Assert.Null(await e.Vencimientos.PanelAsync(f.Admin, borrador.Id));
    }

    [Fact]
    public async Task El_panel_de_un_documento_vencido_lo_marca_asi()
    {
        var (e, f) = await ListoAsync(dias: -4);
        using var _ = e;

        var panel = await e.Vencimientos.PanelAsync(f.Autor, f.RevisionId);

        Assert.Equal(EstadoVencimiento.Vencido, panel!.Vencimiento);
        Assert.Equal(0, panel.DiasRestantes);
        Assert.True(panel.PuedeProponer);
    }

    [Fact]
    public async Task El_historial_lo_ven_quienes_participan_o_gestionan_y_no_cualquiera()
    {
        var (e, f) = await ListoAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, Texto);

        foreach (var permitido in new[] { f.Autor, f.Admin, f.Aprobador, await e.NuevoActorAsync([RolSistema.Auditor]) })
        {
            Assert.Single(await e.Vencimientos.HistorialAsync(permitido, f.RevisionId));
        }

        foreach (var negado in new[] { f.Revisor, await e.NuevoActorAsync([]), await e.NuevoAutorAsync(Escenario.CodigoUnico("Q")) })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.HistorialAsync(negado, f.RevisionId));
        }
    }
}
