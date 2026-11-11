using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Reportes;

/// <summary>Historial completo de un documento: revisiones, envíos, decisiones por etapa y solicitudes «sin cambios».</summary>
public class HistorialDocumentoTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    /// <summary>
    /// Rev.00: rechazada en el primer envío y aprobada en el segundo (queda obsoleta). Rev.01: vigente, con una solicitud
    /// «sin cambios» pendiente. Rev.02: borrador cancelado. Con dueño de área.
    /// </summary>
    private async Task<(Escenario E, EscenarioFlujo F, int Rev01)> HistoriaCompletaAsync()
    {
        var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);

        await e.IniciarAsync(f);
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, false, "Falta el anexo técnico");
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId);

        var rev01 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Actualización de equipos", "Cambia el torque", false);
        await e.EnviarRevisionAsync(f, rev01.Id);
        await e.AprobarRevisionAsync(f, rev01.Id);

        var rev02 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Idea descartada", null, false);
        await e.Revisiones.CancelarBorradorAsync(f.Autor, rev02.Id, "Ya no hace falta");

        await e.IrASiguienteRevisionAsync(f, rev01.Id, 10);
        await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, rev01.Id, "Revisado, sigue igual");
        return (e, f, rev01.Id);
    }

    [Fact]
    public async Task El_historial_trae_las_tres_revisiones_en_orden_con_su_estado_y_su_motivo()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;

        var h = await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id);

        Assert.Equal(f.Documento.Codigo, h.Codigo);
        Assert.Equal(EstadoDocumento.Activo, h.Estado);
        Assert.Equal(["Rev.00", "Rev.01", "Rev.02"], h.Revisiones.Select(r => r.Etiqueta));
        Assert.Equal([EstadoRevision.Obsoleto, EstadoRevision.Vigente, EstadoRevision.Cancelado], h.Revisiones.Select(r => r.Estado));
        Assert.Equal("Actualización de equipos", h.Revisiones[1].MotivoCambio);
        Assert.Equal("Cambia el torque", h.Revisiones[1].DescripcionCambio);
        Assert.NotNull(h.Revisiones[1].FechaProximaRevisionUtc);
        Assert.StartsWith(f.CodigoArea, h.Area);
        Assert.False(string.IsNullOrWhiteSpace(h.Tipo));
        Assert.StartsWith("Usuario ", h.Responsable);
    }

    [Fact]
    public async Task Cada_envio_trae_sus_etapas_con_quien_decidio_cuando_y_con_que_comentario()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;

        var rev00 = (await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id)).Revisiones[0];

        Assert.Equal(2, rev00.Envios.Count);
        var primero = rev00.Envios[0];
        Assert.Equal((1, EstadoFlujo.Rechazado), (primero.Numero, primero.Estado));
        Assert.Equal(EstadoPaso.Rechazado, primero.Pasos[0].Estado);
        Assert.Equal("Falta el anexo técnico", primero.Pasos[0].Comentario);
        Assert.NotNull(primero.Pasos[0].DecididoPor);
        Assert.NotNull(primero.Pasos[0].FechaDecisionUtc);
        Assert.Equal(EstadoPaso.Omitido, primero.Pasos[1].Estado);   // la aprobación nunca llegó a ejecutarse
        Assert.Null(primero.Pasos[1].DecididoPor);

        var segundo = rev00.Envios[1];
        Assert.Equal((2, EstadoFlujo.Aprobado), (segundo.Numero, segundo.Estado));
        Assert.All(segundo.Pasos, p => Assert.Equal(EstadoPaso.Aprobado, p.Estado));
        Assert.Equal(["Revisión técnica", "Aprobación"], segundo.Pasos.Select(p => p.Nombre));
    }

    [Fact]
    public async Task Una_revision_cancelada_sin_envios_tambien_aparece()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;

        var rev02 = (await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id)).Revisiones[2];

        Assert.Empty(rev02.Envios);
        Assert.Equal("Idea descartada", rev02.MotivoCambio);
    }

    [Fact]
    public async Task Las_solicitudes_sin_cambios_cuelgan_de_la_revision_a_la_que_pertenecen()
    {
        var (e, f, rev01) = await HistoriaCompletaAsync();
        using var _ = e;

        var h = await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id);

        Assert.Empty(h.Revisiones[0].SinCambios);
        var s = Assert.Single(h.Revisiones[1].SinCambios);
        Assert.Equal(rev01, s.RevisionId);
        Assert.Equal(EstadoSolicitudSinCambios.Pendiente, s.Estado);
        Assert.Equal("Revisado, sigue igual", s.Comentario);
    }

    [Fact]
    public async Task La_exportacion_del_historial_incluye_todas_las_etapas_y_la_solicitud()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;
        var h = await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id);

        var csv = System.Text.Encoding.UTF8.GetString(e.Exportador.ACsv(TablasReporte.Historial(h)));

        Assert.Contains("Falta el anexo técnico", csv);
        Assert.Contains("Omitida", csv);
        Assert.Contains("Revisado sin cambios", csv);
        Assert.Contains("Cancelado", csv);                       // Rev.02, sin envíos, también ocupa su fila
        Assert.Contains("Idea descartada", csv);
    }

    // ---------- Quién puede verlo ----------

    [Fact]
    public async Task Lo_ven_los_gestores_el_dueno_y_quien_tuvo_una_etapa()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var actor in new[] { f.Admin, auditor, f.Autor, f.Aprobador /* dueño */, f.Revisor /* tuvo etapa */ })
        {
            Assert.Equal(f.Documento.Codigo, (await e.Reportes.HistorialDocumentoAsync(actor, f.Documento.Id)).Codigo);
        }
    }

    [Fact]
    public async Task No_lo_ven_los_ajenos_al_documento()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;
        var autorAjeno = await e.NuevoAutorAsync(Escenario.CodigoUnico("Z"));
        var lector = await e.NuevoActorAsync([]);
        var aprobadorSinEtapa = await e.NuevoAprobadorAsync();

        foreach (var actor in new[] { autorAjeno, lector, aprobadorSinEtapa })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Reportes.HistorialDocumentoAsync(actor, f.Documento.Id));
        }
    }

    [Fact]
    public async Task Un_documento_inexistente_se_dice_claro()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Reportes.HistorialDocumentoAsync(admin, int.MaxValue));
    }

    [Fact]
    public async Task Un_documento_retirado_conserva_su_historial_consultable()
    {
        var (e, f, _) = await HistoriaCompletaAsync();
        using var _ = e;
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");

        var h = await e.Reportes.HistorialDocumentoAsync(f.Admin, f.Documento.Id);

        Assert.Equal(EstadoDocumento.Retirado, h.Estado);
        Assert.Equal(3, h.Revisiones.Count);
        Assert.Equal(EstadoSolicitudSinCambios.Cancelada, h.Revisiones[1].SinCambios.Single().Estado); // el retiro la cerró
    }
}
