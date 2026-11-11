using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Reportes;

/// <summary>Reporte de tareas pendientes por persona: etapas de flujo y solicitudes «revisado sin cambios» por decidir.</summary>
public class TareasReporteTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static FiltroTareas DeArea(EscenarioFlujo f) => new(AreaId: f.AreaId);

    /// <summary>Documento enviado a revisión: la etapa vigente es la del revisor.</summary>
    private async Task<(Escenario E, EscenarioFlujo F)> EnviadoAsync()
    {
        var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        return (e, f);
    }

    /// <summary>Documento publicado cuya revisión periódica ya toca (faltan 10 días), con dueño de área opcional.</summary>
    private async Task<(Escenario E, EscenarioFlujo F)> ParaSinCambiosAsync(bool conDueno = true)
    {
        var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        if (conDueno)
        {
            await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        }

        await e.PublicarPrimeraAsync(f);
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 10);
        return (e, f);
    }

    // ---------- Etapas de flujo ----------

    [Fact]
    public async Task Al_enviar_a_revision_solo_figura_la_etapa_vigente_con_los_dias_que_lleva_esperando()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        e.Reloj.Advance(TimeSpan.FromDays(4));

        var r = await e.Reportes.TareasAsync(f.Admin, DeArea(f));

        var fila = Assert.Single(r.Filas);                       // el aprobador todavía no tiene nada
        Assert.Equal(f.RevisorId, fila.UsuarioId);
        Assert.Equal(TipoTareaReporte.Revision, fila.Tipo);
        Assert.Equal("Revisión técnica", fila.Detalle);
        Assert.Equal(f.Documento.Codigo, fila.Codigo);
        Assert.Equal("Rev.00", fila.Etiqueta);
        Assert.Equal(4, fila.DiasEsperando);
    }

    [Fact]
    public async Task Al_aprobar_una_etapa_la_tarea_pasa_a_la_siguiente_persona_y_los_dias_cuentan_desde_ese_momento()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        e.Reloj.Advance(TimeSpan.FromDays(3));
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, true, null);
        e.Reloj.Advance(TimeSpan.FromDays(2));

        var r = await e.Reportes.TareasAsync(f.Admin, DeArea(f));

        var fila = Assert.Single(r.Filas);
        Assert.Equal(f.AprobadorId, fila.UsuarioId);
        Assert.Equal(TipoTareaReporte.Aprobacion, fila.Tipo);
        Assert.Equal(2, fila.DiasEsperando);                     // no 5: el aprobador espera desde que el revisor terminó
    }

    [Fact]
    public async Task Un_rechazo_o_un_flujo_cancelado_no_dejan_tareas()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, false, "Falta el anexo");
        Assert.Empty((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);

        await e.IniciarAsync(f);
        Assert.Single((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);
        await e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "Se reenvía después");

        Assert.Empty((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);
    }

    [Fact]
    public async Task Al_retirar_el_documento_sus_tareas_desaparecen()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");

        Assert.Empty((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);
    }

    [Fact]
    public async Task Una_reasignacion_mueve_la_tarea_a_la_persona_nueva()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        var otro = await e.NuevoRevisorAsync();
        var idOtro = await e.IdDeAsync(otro);

        await e.Flujo.ReasignarAsync(f.Autor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, idOtro, "Está de vacaciones");

        var fila = Assert.Single((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);
        Assert.Equal(idOtro, fila.UsuarioId);
    }

    // ---------- Resumen y filtros ----------

    [Fact]
    public async Task El_resumen_por_persona_suma_sus_tareas_y_marca_la_mas_antigua()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        e.Reloj.Advance(TimeSpan.FromDays(5));
        var segundo = await e.Documentos.CrearAsync(f.Autor, new("Segundo documento", f.AreaId, null, f.TipoId));
        await e.SubirPdfAsync(f.Autor, segundo.Revisiones.Single().Id, "otro");
        await e.Flujo.IniciarAsync(f.Autor, segundo.Revisiones.Single().Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);

        var r = await e.Reportes.TareasAsync(f.Admin, DeArea(f));

        var resumen = Assert.Single(r.PorUsuario);
        Assert.Equal(f.RevisorId, resumen.UsuarioId);
        Assert.Equal(2, resumen.Total);
        Assert.Equal(5, resumen.DiasMaximo);
        Assert.Equal([f.Documento.Codigo, segundo.Codigo], r.Filas.Select(x => x.Codigo)); // la más antigua primero
    }

    [Fact]
    public async Task El_filtro_de_persona_acota_las_filas_pero_no_el_resumen()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, true, null);
        var otro = await e.Documentos.CrearAsync(f.Autor, new("Otro", f.AreaId, null, f.TipoId));
        await e.SubirPdfAsync(f.Autor, otro.Revisiones.Single().Id, "x");
        await e.Flujo.IniciarAsync(f.Autor, otro.Revisiones.Single().Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);

        var r = await e.Reportes.TareasAsync(f.Admin, new FiltroTareas(UsuarioId: f.AprobadorId, AreaId: f.AreaId));

        Assert.All(r.Filas, x => Assert.Equal(f.AprobadorId, x.UsuarioId));
        Assert.Single(r.Filas);
        Assert.Equal(2, r.PorUsuario.Count);                     // el selector sigue mostrando a las dos personas
    }

    [Fact]
    public async Task El_filtro_de_tipo_acota_y_el_resumen_refleja_ese_tipo()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;

        var soloAprobaciones = await e.Reportes.TareasAsync(f.Admin, new FiltroTareas(Tipo: TipoTareaReporte.Aprobacion, AreaId: f.AreaId));
        var soloRevisiones = await e.Reportes.TareasAsync(f.Admin, new FiltroTareas(Tipo: TipoTareaReporte.Revision, AreaId: f.AreaId));

        Assert.Empty(soloAprobaciones.Filas);
        Assert.Empty(soloAprobaciones.PorUsuario);
        Assert.Single(soloRevisiones.Filas);
    }

    // ---------- «Revisado sin cambios» por decidir ----------

    [Fact]
    public async Task Una_solicitud_pendiente_figura_a_nombre_del_dueno_del_area()
    {
        var (e, f) = await ParaSinCambiosAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");
        e.Reloj.Advance(TimeSpan.FromDays(2));

        var r = await e.Reportes.TareasAsync(f.Admin, DeArea(f));

        var fila = Assert.Single(r.Filas);
        Assert.Equal(TipoTareaReporte.DecisionSinCambios, fila.Tipo);
        Assert.Equal(f.AprobadorId, fila.UsuarioId);
        Assert.StartsWith("Propuesto por ", fila.Detalle);
        Assert.Equal(2, fila.DiasEsperando);
    }

    [Fact]
    public async Task Sin_dueno_de_area_la_decision_figura_a_nombre_de_los_administradores()
    {
        var (e, f) = await ParaSinCambiosAsync(conDueno: false);
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");

        var fila = Assert.Single((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);

        Assert.Equal(Guid.Empty, fila.UsuarioId);
        Assert.Contains("Administradores", fila.Usuario);
    }

    [Fact]
    public async Task Si_el_dueno_esta_inactivo_la_decision_pasa_a_los_administradores()
    {
        var (e, f) = await ParaSinCambiosAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");
        await using (var db = e.Db())
        {
            await db.Usuarios.Where(u => u.Id == f.AprobadorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Activo, false));
        }

        var fila = Assert.Single((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);

        Assert.Equal(Guid.Empty, fila.UsuarioId);
    }

    [Fact]
    public async Task Una_solicitud_resuelta_ya_no_es_tarea()
    {
        var (e, f) = await ParaSinCambiosAsync();
        using var _ = e;
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);

        Assert.Empty((await e.Reportes.TareasAsync(f.Admin, DeArea(f))).Filas);
    }

    [Fact]
    public async Task El_filtro_de_tipo_separa_las_etapas_de_las_decisiones()
    {
        var (e, f) = await ParaSinCambiosAsync();
        using var _ = e;
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");

        var decisiones = await e.Reportes.TareasAsync(f.Admin, new FiltroTareas(Tipo: TipoTareaReporte.DecisionSinCambios, AreaId: f.AreaId));
        var etapas = await e.Reportes.TareasAsync(f.Admin, new FiltroTareas(Tipo: TipoTareaReporte.Revision, AreaId: f.AreaId));

        Assert.Single(decisiones.Filas);
        Assert.Empty(etapas.Filas);
    }

    // ---------- Alcance por rol ----------

    [Fact]
    public async Task Administrador_auditor_y_autor_del_area_ven_las_tareas_y_el_autor_de_otra_area_no()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        var autorAjeno = await e.NuevoAutorAsync(Escenario.CodigoUnico("Z"));

        foreach (var actor in new[] { f.Admin, auditor, f.Autor })
        {
            Assert.Single((await e.Reportes.TareasAsync(actor, DeArea(f))).Filas);
        }

        Assert.Empty((await e.Reportes.TareasAsync(autorAjeno, DeArea(f))).Filas);
    }

    [Fact]
    public async Task El_dueno_del_area_ve_las_tareas_de_su_area()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        var dueno = await e.NuevoAprobadorAsync();
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, await e.IdDeAsync(dueno));

        Assert.Single((await e.Reportes.TareasAsync(dueno, DeArea(f))).Filas);
    }

    [Fact]
    public async Task Quien_no_tiene_area_o_solo_trabaja_sus_tareas_no_ve_el_reporte()
    {
        var (e, f) = await EnviadoAsync();
        using var _ = e;
        var lector = await e.NuevoActorAsync([]);
        var aprobadorNoDueno = await e.NuevoAprobadorAsync();

        foreach (var actor in new[] { f.Revisor, lector, aprobadorNoDueno, await e.NuevoAutorAsync() })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Reportes.TareasAsync(actor, new FiltroTareas()));
        }
    }
}
