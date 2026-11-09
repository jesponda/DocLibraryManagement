using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Flujo;

public class FlujoServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    // ---------- Plantillas ----------

    [Fact]
    public async Task El_Administrador_guarda_la_plantilla_y_se_reemplaza_en_bloque()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Flujo.GuardarPlantillaAsync(f.Admin, f.TipoId,
        [
            new PasoPlantillaDto(0, TipoPaso.Revision, "Calidad"),
            new PasoPlantillaDto(0, TipoPaso.Revision, "Seguridad"),
            new PasoPlantillaDto(0, TipoPaso.Aprobacion, "Gerencia"),
        ]);

        var plantilla = await e.Flujo.ObtenerPlantillaAsync(f.TipoId);
        Assert.Equal(["Calidad", "Seguridad", "Gerencia"], plantilla.Select(p => p.Nombre));
        Assert.Equal([1, 2, 3], plantilla.Select(p => p.Orden));
        Assert.Contains((await e.AuditoriaDeAsync((await TipoCodigo(e, f.TipoId)))), x => x.Accion == "Flujo.GuardarPlantilla");
    }

    private static async Task<string> TipoCodigo(Escenario e, int tipoId)
    {
        await using var db = e.Db();
        return (await db.TiposDocumento.SingleAsync(t => t.Id == tipoId)).Codigo;
    }

    [Theory]
    [InlineData(RolSistema.Autor)]
    [InlineData(RolSistema.Aprobador)]
    [InlineData(RolSistema.Auditor)]
    public async Task Solo_el_Administrador_modifica_plantillas(RolSistema rol)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var actor = await e.NuevoActorAsync([rol], f.CodigoArea);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
            e.Flujo.GuardarPlantillaAsync(actor, f.TipoId, [new PasoPlantillaDto(1, TipoPaso.Aprobacion, "x")]));
    }

    [Fact]
    public async Task Una_plantilla_invalida_no_toca_la_existente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.GuardarPlantillaAsync(f.Admin, f.TipoId, [new PasoPlantillaDto(1, TipoPaso.Revision, "sin aprobar")]));

        Assert.Equal(2, (await e.Flujo.ObtenerPlantillaAsync(f.TipoId)).Count);
    }

    [Fact]
    public async Task Cambiar_la_plantilla_no_afecta_a_un_flujo_ya_iniciado()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.Flujo.GuardarPlantillaAsync(f.Admin, f.TipoId, [new PasoPlantillaDto(1, TipoPaso.Aprobacion, "Solo gerencia")]);

        var flujo = await e.UltimoFlujoAsync(f);
        Assert.Equal(["Revisión técnica", "Aprobación"], flujo.Pasos.Select(p => p.Nombre));
    }

    // ---------- Candidatos ----------

    [Fact]
    public async Task Los_candidatos_son_quienes_tuvieron_el_rol_en_su_ultimo_acceso()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var lector = await e.NuevoActorAsync([]);

        var revisores = await e.Flujo.CandidatosAsync(TipoPaso.Revision);
        var aprobadores = await e.Flujo.CandidatosAsync(TipoPaso.Aprobacion);

        Assert.Contains(revisores, c => c.UsuarioId == f.RevisorId);
        Assert.DoesNotContain(revisores, c => c.UsuarioId == f.AprobadorId);
        Assert.Contains(aprobadores, c => c.UsuarioId == f.AprobadorId);
        Assert.DoesNotContain(revisores.Concat(aprobadores), c => c.Identificador == lector.Identificador);
    }

    [Fact]
    public async Task Quien_pierde_el_rol_deja_de_ser_candidato_en_cuanto_vuelve_a_entrar()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        Assert.Contains(await e.Flujo.CandidatosAsync(TipoPaso.Revision), c => c.UsuarioId == f.RevisorId);

        await e.Usuarios.SincronizarAsync(f.Revisor.Identificador, "Ex revisor", null, [RolSistema.Lector], []);

        Assert.DoesNotContain(await e.Flujo.CandidatosAsync(TipoPaso.Revision), c => c.UsuarioId == f.RevisorId);
    }

    // ---------- Iniciar ----------

    [Fact]
    public async Task Enviar_a_revision_copia_la_plantilla_asigna_personas_y_pone_la_revision_En_revision()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.IniciarAsync(f);

        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId));
        var flujo = await e.UltimoFlujoAsync(f);
        Assert.Equal(1, flujo.Numero);
        Assert.Equal(EstadoFlujo.EnCurso, flujo.Estado);
        Assert.Equal([TipoPaso.Revision, TipoPaso.Aprobacion], flujo.Pasos.Select(p => p.Clase));
        Assert.Equal([f.RevisorId, f.AprobadorId], flujo.Pasos.Select(p => p.AsignadoAId));
        Assert.All(flujo.Pasos, p => Assert.Equal(EstadoPaso.Pendiente, p.Estado));
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Flujo.Iniciar" && x.Usuario == f.Autor.Identificador);
    }

    [Fact]
    public async Task No_se_envia_sin_PDF()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.IniciarAsync(f));
        Assert.Contains("PDF", ex.Message);
        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(f.RevisionId));
    }

    [Fact]
    public async Task No_se_envia_si_el_tipo_no_tiene_flujo_configurado()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPlantilla: false);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.IniciarAsync(f));
        Assert.Contains("flujo configurado", ex.Message);
    }

    [Fact]
    public async Task El_autor_de_la_revision_no_puede_ser_asignado_ni_siquiera_con_el_rol()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        // Un autor que además es revisor y aprobador: el caso típico de quien quiere aprobarse a sí mismo.
        var autorTodoterreno = await e.NuevoActorAsync([RolSistema.Autor, RolSistema.Revisor, RolSistema.Aprobador], f.CodigoArea);
        var doc = await e.Documentos.CrearAsync(autorTodoterreno, new("Mío", f.AreaId, null, f.TipoId));
        var revId = doc.Revisiones.Single().Id;
        await e.SubirPdfAsync(autorTodoterreno, revId);
        var yo = await e.IdDeAsync(autorTodoterreno);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.IniciarAsync(autorTodoterreno, revId, [new AsignacionPaso(1, yo), new AsignacionPaso(2, f.AprobadorId)]));
        Assert.Contains("separación de funciones", ex.Message);

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.IniciarAsync(autorTodoterreno, revId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, yo)]));
        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(revId));
    }

    [Fact]
    public async Task Se_rechaza_asignar_a_alguien_sin_el_rol_de_la_etapa()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var lector = await e.NuevoActorAsync([]);
        var idLector = await e.IdDeAsync(lector);

        // Un aprobador en la etapa de revisión y viceversa también están mal.
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, idLector), new AsignacionPaso(2, f.AprobadorId)]));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, f.AprobadorId), new AsignacionPaso(2, f.AprobadorId)]));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, Guid.NewGuid())]));
    }

    [Fact]
    public async Task Cada_etapa_lleva_exactamente_una_persona()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, f.RevisorId)]));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.IniciarAsync(f.Autor, f.RevisionId,
            [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.IniciarAsync(f.Autor, f.RevisionId,
            [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId), new AsignacionPaso(3, f.AprobadorId)]));
    }

    [Fact]
    public async Task Solo_quien_edita_en_el_area_envia_a_revision()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var ajeno = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
            e.Flujo.IniciarAsync(ajeno, f.RevisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]));
        // El revisor, aunque tenga el rol, no puede enviar el documento de otro.
        await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
            e.Flujo.IniciarAsync(f.Revisor, f.RevisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]));
    }

    [Fact]
    public async Task No_se_envia_dos_veces_la_misma_revision()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.IniciarAsync(f));
        Assert.Single(await e.Flujo.HistorialAsync(f.Autor, f.RevisionId));
    }

    [Fact]
    public async Task Envios_simultaneos_de_la_misma_revision_crean_un_solo_flujo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var intentos = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var otro = new Escenario(bd);
            try
            {
                await otro.Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]);
                return true;
            }
            catch (ReglaNegocioException)
            {
                return false; // los perdedores reciben un mensaje de negocio, no un error técnico
            }
        })).ToArray();

        var resultados = await Task.WhenAll(intentos);

        Assert.Equal(1, resultados.Count(r => r));
        Assert.Single(await e.Flujo.HistorialAsync(f.Autor, f.RevisionId));
    }

    // ---------- Decidir: camino feliz ----------

    [Fact]
    public async Task Aprobar_las_dos_etapas_completa_el_flujo_y_publica_la_revision()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, aprobar: true, "Todo en orden");
        Assert.Equal(EstadoRevision.EnAprobacion, await e.EstadoRevisionAsync(f.RevisionId));

        await e.Flujo.DecidirAsync(f.Aprobador, pasos[1].Id, aprobar: true, null);

        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId));
        var flujo = await e.UltimoFlujoAsync(f);
        Assert.Equal(EstadoFlujo.Aprobado, flujo.Estado);
        Assert.NotNull(flujo.FechaFinUtc);
        Assert.All(flujo.Pasos, p => Assert.Equal(EstadoPaso.Aprobado, p.Estado));
        Assert.Equal("Todo en orden", flujo.Pasos[0].Comentario);
        Assert.Null(flujo.Pasos[1].Comentario);
        await using var db = e.Db();
        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime, (await db.Revisiones.SingleAsync(r => r.Id == f.RevisionId)).FechaAprobacionUtc);
        var acciones = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Select(x => x.Accion).ToList();
        Assert.Equal(2, acciones.Count(a => a == "Flujo.Aprobar"));
    }

    [Fact]
    public async Task Un_flujo_de_tres_etapas_cambia_el_estado_segun_la_etapa_vigente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Flujo.GuardarPlantillaAsync(f.Admin, f.TipoId,
        [
            new PasoPlantillaDto(1, TipoPaso.Revision, "Calidad"),
            new PasoPlantillaDto(2, TipoPaso.Revision, "Seguridad"),
            new PasoPlantillaDto(3, TipoPaso.Aprobacion, "Gerencia"),
        ]);
        var revisor2 = await e.NuevoRevisorAsync();
        await e.Flujo.IniciarAsync(f.Autor, f.RevisionId,
            [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, await e.IdDeAsync(revisor2)), new AsignacionPaso(3, f.AprobadorId)]);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);
        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId)); // sigue otra revisión
        await e.Flujo.DecidirAsync(revisor2, pasos[1].Id, true, null);
        Assert.Equal(EstadoRevision.EnAprobacion, await e.EstadoRevisionAsync(f.RevisionId));
    }

    // ---------- Decidir: rechazo ----------

    [Fact]
    public async Task Rechazar_exige_comentario_y_devuelve_la_revision_a_Borrador_omitiendo_el_resto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, aprobar: false, "  "));
        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId)); // el intento fallido no cambió nada

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, aprobar: false, "Falta el paso de bloqueo de energía");

        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(f.RevisionId));
        var flujo = await e.UltimoFlujoAsync(f);
        Assert.Equal(EstadoFlujo.Rechazado, flujo.Estado);
        Assert.Equal([EstadoPaso.Rechazado, EstadoPaso.Omitido], flujo.Pasos.Select(p => p.Estado));
        var rev = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Contains("bloqueo de energía", rev.ComentarioUltimoRechazo);
    }

    [Fact]
    public async Task Tras_un_rechazo_el_autor_corrige_y_reenvia_en_un_flujo_nuevo_con_historial()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, false, "Corregir sección 3");

        await e.SubirPdfAsync(f.Autor, f.RevisionId, "corregido"); // vuelve a ser editable
        await e.IniciarAsync(f);

        var historial = await e.Flujo.HistorialAsync(f.Autor, f.RevisionId);
        Assert.Equal([2, 1], historial.Select(h => h.Numero));
        Assert.Equal([EstadoFlujo.EnCurso, EstadoFlujo.Rechazado], historial.Select(h => h.Estado));
        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId));
        Assert.Null((await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).ComentarioUltimoRechazo); // ya no es borrador
    }

    // ---------- Decidir: reglas ----------

    [Fact]
    public async Task Solo_la_persona_asignada_decide()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];
        var otroRevisor = await e.NuevoRevisorAsync();

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.DecidirAsync(otroRevisor, paso.Id, true, null));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.DecidirAsync(f.Admin, paso.Id, true, null));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.DecidirAsync(f.Autor, paso.Id, true, null));
    }

    [Fact]
    public async Task No_se_decide_fuera_de_turno()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.DecidirAsync(f.Aprobador, pasos[1].Id, true, null));
        Assert.Contains("turno", ex.Message);
        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId));
    }

    [Fact]
    public async Task Se_revalida_el_rol_vivo_aunque_la_copia_guardada_siga_diciendo_que_lo_tiene()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];

        // Su grupo de AD ya no incluye Revisor (el Actor trae los roles vigentes), pero en la BD la copia es vieja.
        var sinRol = f.Revisor with { Roles = new HashSet<RolSistema> { RolSistema.Lector } };

        var ex = await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.DecidirAsync(sinRol, paso.Id, true, null));
        Assert.Contains("Revisor", ex.Message);
    }

    [Fact]
    public async Task El_autor_no_puede_decidir_aunque_la_BD_lo_tenga_asignado()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];
        var autorId = await e.IdDeAsync(f.Autor);
        // Se fuerza el dato inconsistente directamente (otra ruta que se saltara la validación al iniciar).
        await using (var db = e.Db())
        {
            await db.PasosInstancia.Where(p => p.Id == paso.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AsignadoAId, autorId));
        }

        var autorConRol = f.Autor with { Roles = new HashSet<RolSistema> { RolSistema.Lector, RolSistema.Autor, RolSistema.Revisor } };
        var ex = await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.DecidirAsync(autorConRol, paso.Id, true, null));
        Assert.Contains("separación de funciones", ex.Message);
    }

    [Fact]
    public async Task Una_etapa_ya_decidida_no_se_vuelve_a_decidir()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];
        await e.Flujo.DecidirAsync(f.Revisor, paso.Id, true, null);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.DecidirAsync(f.Revisor, paso.Id, false, "arrepentido"));
    }

    [Fact]
    public async Task Decisiones_simultaneas_sobre_la_misma_etapa_solo_dejan_pasar_una()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];

        var intentos = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            using var otro = new Escenario(bd);
            try
            {
                await otro.Flujo.DecidirAsync(f.Revisor, paso.Id, true, null);
                return true;
            }
            catch (ReglaNegocioException)
            {
                return false;
            }
        })).ToArray();

        var ok = (await Task.WhenAll(intentos)).Count(r => r);

        Assert.Equal(1, ok);
        var auditoria = await e.AuditoriaDeAsync(f.Documento.Codigo);
        Assert.Equal(1, auditoria.Count(a => a.Accion == "Flujo.Aprobar"));
    }

    // ---------- Tareas ----------

    [Fact]
    public async Task Mis_tareas_muestra_solo_la_etapa_vigente_de_cada_flujo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;

        var revisor = await e.Flujo.MisTareasAsync(f.Revisor);
        var aprobador = await e.Flujo.MisTareasAsync(f.Aprobador);

        Assert.Equal(pasos[0].Id, Assert.Single(revisor).PasoId);
        Assert.Empty(aprobador); // todavía no le toca

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);

        Assert.Empty(await e.Flujo.MisTareasAsync(f.Revisor));
        var tarea = Assert.Single(await e.Flujo.MisTareasAsync(f.Aprobador));
        Assert.Equal(f.Documento.Codigo, tarea.Codigo);
        Assert.Equal("Rev.00", tarea.Etiqueta);
        Assert.Equal(TipoPaso.Aprobacion, tarea.Clase);
    }

    [Fact]
    public async Task Un_flujo_cancelado_o_rechazado_no_deja_tareas_pendientes()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "me equivoqué de PDF");

        Assert.Empty(await e.Flujo.MisTareasAsync(f.Revisor));
    }

    // ---------- Reasignar ----------

    [Fact]
    public async Task Reasignar_cambia_a_la_persona_exige_motivo_y_queda_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var paso = (await e.UltimoFlujoAsync(f)).Pasos[0];
        var nuevo = await e.NuevoRevisorAsync();
        var idNuevo = await e.IdDeAsync(nuevo);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.ReasignarAsync(f.Autor, paso.Id, idNuevo, " "));
        await e.Flujo.ReasignarAsync(f.Autor, paso.Id, idNuevo, "El titular está de vacaciones");

        Assert.Empty(await e.Flujo.MisTareasAsync(f.Revisor));
        Assert.Single(await e.Flujo.MisTareasAsync(nuevo));
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Flujo.Reasignar");
        Assert.Equal("El titular está de vacaciones", evento.Comentario);
    }

    [Fact]
    public async Task Reasignar_respeta_el_rol_el_autor_el_permiso_y_el_estado_de_la_etapa()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;
        var ajeno = await e.NuevoAutorAsync("OTRA");
        var idAutor = await e.IdDeAsync(f.Autor);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.ReasignarAsync(f.Autor, pasos[0].Id, f.AprobadorId, "rol equivocado"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.ReasignarAsync(f.Autor, pasos[0].Id, idAutor, "a mí mismo"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.ReasignarAsync(ajeno, pasos[0].Id, f.RevisorId, "no es mi área"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.ReasignarAsync(f.Revisor, pasos[0].Id, f.RevisorId, "yo mismo"));

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.ReasignarAsync(f.Autor, pasos[0].Id, f.RevisorId, "ya decidida"));
    }

    // ---------- Cancelar flujo ----------

    [Fact]
    public async Task Cancelar_el_flujo_devuelve_la_revision_a_Borrador_y_permite_editar_y_reenviar()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, " "));
        await e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "PDF equivocado");

        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(f.RevisionId));
        var flujo = await e.UltimoFlujoAsync(f);
        Assert.Equal(EstadoFlujo.Cancelado, flujo.Estado);
        Assert.All(flujo.Pasos, p => Assert.Equal(EstadoPaso.Omitido, p.Estado));

        await e.SubirPdfAsync(f.Autor, f.RevisionId, "correcto");
        await e.IniciarAsync(f);
        Assert.Equal(2, (await e.Flujo.HistorialAsync(f.Autor, f.RevisionId)).Count);
    }

    [Fact]
    public async Task Cancelar_exige_permiso_y_un_flujo_en_curso()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var ajeno = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "no hay flujo"));
        await e.IniciarAsync(f);
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.CancelarFlujoAsync(ajeno, f.RevisionId, "ajeno"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.CancelarFlujoAsync(f.Revisor, f.RevisionId, "el revisor no cancela"));
    }

    [Fact]
    public async Task No_se_cancela_un_flujo_ya_terminado()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;
        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);
        await e.Flujo.DecidirAsync(f.Aprobador, pasos[1].Id, true, null);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "tarde"));
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId));
    }

    // ---------- Visibilidad del historial ----------

    [Fact]
    public async Task El_historial_lo_ven_gestores_y_asignados_pero_no_terceros()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        var tercero = await e.NuevoRevisorAsync();

        foreach (var quien in new[] { f.Autor, f.Admin, auditor, f.Revisor, f.Aprobador })
        {
            Assert.Single(await e.Flujo.HistorialAsync(quien, f.RevisionId));
        }

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.HistorialAsync(tercero, f.RevisionId));
    }

    [Fact]
    public async Task Quien_tiene_una_etapa_asignada_puede_ver_el_documento_pero_un_tercero_no()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var tercero = await e.NuevoRevisorAsync();

        Assert.NotNull(await e.Documentos.ObtenerAsync(f.Revisor, f.Documento.Id));
        Assert.NotNull(await e.Revisiones.ObtenerAsync(f.Aprobador, f.RevisionId));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(tercero, f.Documento.Id));
    }
}
