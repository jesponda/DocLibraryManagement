using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Correo;

/// <summary>Qué avisos se encolan, a quién y cuándo, en cada evento del flujo.</summary>
public class NotificacionesTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static List<Notificacion> De(List<Notificacion> todas, string tipo) => [.. todas.Where(n => n.Tipo == tipo)];

    // ---------- Apagado por defecto ----------

    [Fact]
    public async Task Con_el_correo_desactivado_no_se_encola_nada_en_todo_el_ciclo()
    {
        using var e = new Escenario(bd); // sin correo: es la configuración por defecto
        var f = await e.PrepararFlujoAsync();

        await e.PublicarPrimeraAsync(f);

        Assert.Empty(await e.NotificacionesDeAsync(f.Documento.Codigo));
    }

    // ---------- Tarea asignada ----------

    [Fact]
    public async Task Enviar_a_revision_avisa_solo_a_quien_tiene_el_primer_turno()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();

        await e.IniciarAsync(f);

        var n = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo)); // el aprobador NO recibe aviso todavía
        Assert.Equal(EventosCorreo.TareaAsignada, n.Tipo);
        Assert.Equal(f.RevisorId, n.DestinatarioId);
        Assert.Equal(EstadoNotificacion.Pendiente, n.Estado);
        Assert.Contains(f.Documento.Codigo, n.Asunto);
        Assert.Contains("Rev.00", n.Asunto);
        Assert.Contains("revisión", n.Asunto);
        Assert.Contains("Revisión técnica", n.Cuerpo);                                 // nombre de la etapa
        Assert.Contains($"https://docs.test/documentos/{f.Documento.Id}", n.Cuerpo);   // enlace directo
        Assert.Contains("Procedimiento de prueba", n.Cuerpo);                          // título
        Assert.Contains(n.Destinatario!.NombreVisible, n.Cuerpo);                      // saluda por su nombre
    }

    [Fact]
    public async Task Al_aprobar_una_etapa_se_avisa_a_la_persona_de_la_siguiente()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);

        var tareas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.TareaAsignada);
        Assert.Equal([f.RevisorId, f.AprobadorId], tareas.Select(t => t.DestinatarioId));
        Assert.Contains("aprobación", tareas[1].Asunto);
    }

    [Fact]
    public async Task Reasignar_avisa_a_la_persona_nueva_y_no_repite_el_aviso_a_la_anterior()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var nuevo = await e.NuevoRevisorAsync();
        var idNuevo = await e.IdDeAsync(nuevo);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];

        await e.Flujo.ReasignarAsync(f.Autor, paso.Id, idNuevo, "El titular está de vacaciones");

        var tareas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.TareaAsignada);
        Assert.Equal([f.RevisorId, idNuevo], tareas.Select(t => t.DestinatarioId)); // el original (al iniciar) y el nuevo
        Assert.Contains("Revisión técnica", tareas[1].Cuerpo);
    }

    [Fact]
    public async Task Una_persona_desactivada_no_recibe_avisos()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await using (var db = e.Db())
        {
            await db.Usuarios.Where(u => u.Id == f.AprobadorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Activo, false));
        }

        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, true, null);

        var tareas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.TareaAsignada);
        Assert.DoesNotContain(tareas, t => t.DestinatarioId == f.AprobadorId);
    }

    // ---------- Rechazo ----------

    [Fact]
    public async Task Un_rechazo_avisa_al_autor_con_quien_lo_rechazo_y_el_motivo()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var autorId = await e.IdDeAsync(f.Autor);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, false, "Falta el paso de bloqueo de energía");

        var rechazo = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.Rechazada));
        Assert.Equal(autorId, rechazo.DestinatarioId);
        Assert.Contains("Falta el paso de bloqueo de energía", rechazo.Cuerpo);
        Assert.Contains("Revisión técnica", rechazo.Cuerpo);
        Assert.Contains("rechazó", rechazo.Cuerpo);
        Assert.Contains(f.Documento.Codigo, rechazo.Asunto);
    }

    [Fact]
    public async Task Una_operacion_que_falla_no_deja_ningun_correo_en_cola()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var antes = (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count;

        // Rechazar sin motivo falla: el aviso de rechazo no debe existir (el correo es parte de la misma transacción).
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.DecidirAsync(f.Revisor, e.UltimoFlujoAsync(f).Result.Pasos[0].Id, false, "  "));

        Assert.Equal(antes, (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count);
    }

    // ---------- Publicación ----------

    [Fact]
    public async Task Al_publicar_avisa_al_autor_que_es_tambien_el_responsable_una_sola_vez_y_no_al_aprobador()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var autorId = await e.IdDeAsync(f.Autor);

        await e.PublicarPrimeraAsync(f);

        var publicadas = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.Publicada);
        var n = Assert.Single(publicadas);                       // responsable == autor: un solo correo
        Assert.Equal(autorId, n.DestinatarioId);
        Assert.Contains($"https://docs.test/doc/{f.Documento.Codigo}", n.Cuerpo); // al visor, no a la gestión
        Assert.Contains("Entra en vigor", n.Cuerpo);
    }

    [Fact]
    public async Task Al_publicar_una_revision_de_otro_autor_se_avisa_a_ambos()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);                                  // Rev.00: el responsable es f.Autor
        var otroAutor = await e.NuevoAutorAsync(f.CodigoArea);
        var nueva = await e.Revisiones.CrearRevisionAsync(otroAutor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.SubirPdfAsync(otroAutor, nueva.Id, "nueva");
        await e.Flujo.IniciarAsync(otroAutor, nueva.Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);

        await e.AprobarRevisionAsync(f, nueva.Id);

        var rev01 = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.Publicada).Where(n => n.Asunto.Contains("Rev.01")).ToList();
        Assert.Equivalent(new[] { await e.IdDeAsync(f.Autor), await e.IdDeAsync(otroAutor) }, rev01.Select(n => n.DestinatarioId));
    }

    [Fact]
    public async Task Quien_publica_no_recibe_el_aviso_de_su_propia_accion()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        // Una persona que es Autor y también Aprobador: crea el documento (queda como responsable) y luego aprueba la Rev.01 de otro.
        var autorAprobador = await e.NuevoActorAsync([RolSistema.Autor, RolSistema.Aprobador], f.CodigoArea);
        var propio = await e.Documentos.CrearAsync(autorAprobador, new("Del autor-aprobador", f.AreaId, null, f.TipoId));
        await e.SubirPdfAsync(autorAprobador, propio.Revisiones.Single().Id);
        await e.Flujo.IniciarAsync(autorAprobador, propio.Revisiones.Single().Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        var f2 = f with { Documento = propio, RevisionId = propio.Revisiones.Single().Id };
        await e.AprobarRevisionAsync(f2, f2.RevisionId);

        var otroAutor = await e.NuevoAutorAsync(f.CodigoArea);
        var nueva = await e.Revisiones.CrearRevisionAsync(otroAutor, propio.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.SubirPdfAsync(otroAutor, nueva.Id, "otra");
        var idAutorAprobador = await e.IdDeAsync(autorAprobador);
        await e.Flujo.IniciarAsync(otroAutor, nueva.Id, [new(1, f.RevisorId), new(2, idAutorAprobador)]);
        var pasos = (await e.Flujo.HistorialAsync(f.Admin, nueva.Id)).First().Pasos;
        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);

        await e.Flujo.DecidirAsync(autorAprobador, pasos[1].Id, true, null); // el responsable aprueba y publica

        var rev01 = De(await e.NotificacionesDeAsync(propio.Codigo), EventosCorreo.Publicada).Where(n => n.Asunto.Contains("Rev.01")).ToList();
        var unico = Assert.Single(rev01);                                   // el responsable es quien actuó: solo avisa al otro autor
        Assert.Equal(await e.IdDeAsync(otroAutor), unico.DestinatarioId);
    }

    [Fact]
    public async Task La_publicacion_diferida_que_hace_el_sistema_tambien_avisa()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(3));
        Assert.Empty(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.Publicada)); // aún no entra en vigor

        e.Reloj.Advance(TimeSpan.FromDays(4));
        await e.Scope().GetRequiredService<IPublicacionService>().ActivarPendientesAsync();

        var n = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.Publicada));
        Assert.Equal(await e.IdDeAsync(f.Autor), n.DestinatarioId);
    }

    // ---------- Aprobación anulada / fecha cambiada ----------

    private static async Task<(EscenarioFlujo F, int RevisionId, DateTime Fecha)> ProgramadaAsync(Escenario e)
    {
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        var fecha = e.Reloj.GetUtcNow().UtcDateTime.AddDays(10);
        await e.AprobarRevisionAsync(f, nueva.Id, fecha);
        return (f, nueva.Id, fecha);
    }

    [Fact]
    public async Task Anular_una_aprobacion_avisa_al_autor_con_el_motivo_y_quien_la_anulo()
    {
        using var e = new Escenario(bd, correo: true);
        var (f, revId, _) = await ProgramadaAsync(e);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Se detectó un error en la sección 4");

        var n = Assert.Single(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AprobacionRevocada));
        Assert.Equal(await e.IdDeAsync(f.Autor), n.DestinatarioId);
        Assert.Contains("Se detectó un error en la sección 4", n.Cuerpo);
        Assert.Contains("anuló", n.Cuerpo);
        Assert.Contains("Rev.01", n.Asunto);
    }

    [Fact]
    public async Task Si_el_administrador_es_quien_elaboro_la_revision_no_se_avisa_a_si_mismo()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Admin, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio del admin", null, false);
        await e.SubirPdfAsync(f.Admin, nueva.Id, "admin");
        await e.Flujo.IniciarAsync(f.Admin, nueva.Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        await e.AprobarRevisionAsync(f, nueva.Id, e.Reloj.GetUtcNow().UtcDateTime.AddDays(10));

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, nueva.Id, "Me corrijo");

        Assert.Empty(De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.AprobacionRevocada));
    }

    [Fact]
    public async Task Cambiar_la_fecha_avisa_al_autor_y_al_responsable_con_la_fecha_nueva_sin_avisar_al_administrador()
    {
        using var e = new Escenario(bd, correo: true);
        var (f, revId, original) = await ProgramadaAsync(e);
        var otroAutor = await e.NuevoAutorAsync(f.CodigoArea); // para que autor y responsable sean personas distintas en otra revisión
        var nueva = original.AddDays(7);

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, nueva, "Se pospone por capacitación");

        var avisos = De(await e.NotificacionesDeAsync(f.Documento.Codigo), EventosCorreo.VigenciaReprogramada);
        var n = Assert.Single(avisos); // autor == responsable (f.Autor): un solo correo
        Assert.Equal(await e.IdDeAsync(f.Autor), n.DestinatarioId);
        Assert.Contains("Se pospone por capacitación", n.Cuerpo);
        Assert.Contains(nueva.ToString("dd/MM/yyyy"), n.Cuerpo);
        Assert.NotEqual(await e.IdDeAsync(otroAutor), n.DestinatarioId);
    }

    // ---------- Contenido y seguridad ----------

    [Fact]
    public async Task Un_titulo_con_saltos_de_linea_no_puede_inyectar_cabeceras_en_el_asunto()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var doc = await e.Documentos.CrearAsync(f.Autor, new("Normal\r\nBcc: espia@externo.com", f.AreaId, null, f.TipoId));
        await e.SubirPdfAsync(f.Autor, doc.Revisiones.Single().Id);

        await e.Flujo.IniciarAsync(f.Autor, doc.Revisiones.Single().Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);

        var n = Assert.Single(await e.NotificacionesDeAsync(doc.Codigo));
        Assert.DoesNotContain('\r', n.Asunto);
        Assert.DoesNotContain('\n', n.Asunto);
        Assert.True(n.Asunto.Length <= 200);
    }

    [Fact]
    public async Task Una_plantilla_configurada_reemplaza_el_texto_predeterminado()
    {
        var extra = new Dictionary<string, string?> { ["Correo:Plantillas:TareaAsignada:Asunto"] = "Aviso para {Destinatario}: {Codigo}" };
        using var e = new Escenario(bd, correo: true, extra: extra);
        var f = await e.PrepararFlujoAsync();

        await e.IniciarAsync(f);

        var n = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo));
        Assert.Equal($"Aviso para {n.Destinatario!.NombreVisible}: {f.Documento.Codigo}", n.Asunto);
        Assert.Contains("Revisión técnica", n.Cuerpo); // el cuerpo no se tocó: sigue el predeterminado
    }

    [Fact]
    public async Task Sin_direccion_publica_configurada_el_enlace_no_queda_roto()
    {
        var extra = new Dictionary<string, string?> { ["Correo:UrlBase"] = "" };
        using var e = new Escenario(bd, correo: true, extra: extra);
        var f = await e.PrepararFlujoAsync();

        await e.IniciarAsync(f);

        var n = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo));
        Assert.DoesNotContain("http", n.Cuerpo);
        Assert.Contains("abre el sistema de gestión documental", n.Cuerpo);
    }
}
