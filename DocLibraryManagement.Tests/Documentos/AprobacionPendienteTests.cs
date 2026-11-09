using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Documentos;

/// <summary>
/// Una revisión aprobada con fecha de vigencia futura (estado Aprobado) puede corregirse antes de entrar en vigor:
/// el Administrador cambia la fecha o anula la aprobación y la devuelve a borrador.
/// </summary>
public class AprobacionPendienteTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    /// <summary>Documento publicado (Rev.00) con una Rev.01 ya aprobada que entrará en vigor dentro de <paramref name="dias"/>.</summary>
    private static async Task<(EscenarioFlujo F, int RevisionId, DateTime Fecha)> ProgramadaAsync(Escenario e, int dias = 10)
    {
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        var fecha = e.Reloj.GetUtcNow().UtcDateTime.AddDays(dias);
        await e.AprobarRevisionAsync(f, nueva.Id, fecha);
        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(nueva.Id)); // punto de partida
        return (f, nueva.Id, fecha);
    }

    // ---------- Cambiar la fecha ----------

    [Fact]
    public async Task El_Administrador_cambia_la_fecha_y_la_nueva_es_la_que_manda()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e, dias: 10);
        var nueva = original.AddDays(10);
        var activador = e.Scope().GetRequiredService<IPublicacionService>();

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, nueva, "Se pospone por capacitación");

        var r = await e.Revisiones.ObtenerAsync(f.Admin, revId);
        Assert.Equal(EstadoRevision.Aprobado, r.Estado);       // la aprobación se conserva
        Assert.Equal(nueva, r.FechaVigenciaUtc);
        e.Reloj.Advance(TimeSpan.FromDays(11));                // pasó la fecha ORIGINAL
        await activador.ActivarPendientesAsync();
        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(revId)); // no se publicó con la fecha vieja
        Assert.Equal(EstadoRevision.Vigente, (await e.EstadosDelDocumentoAsync(f.Documento.Id))["Rev.00"]);

        e.Reloj.Advance(TimeSpan.FromDays(10));                // ahora sí pasó la nueva
        await activador.ActivarPendientesAsync();
        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.01"]);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(nueva, (await e.Revisiones.ObtenerAsync(f.Admin, revId)).FechaVigenciaUtc); // se publica con la fecha programada
    }

    [Fact]
    public async Task Tambien_se_puede_adelantar_la_fecha_siempre_que_siga_siendo_futura()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e, dias: 30);
        var adelantada = e.Reloj.GetUtcNow().UtcDateTime.AddDays(2);

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, adelantada, "Se necesita antes");
        e.Reloj.Advance(TimeSpan.FromDays(3));
        await e.Scope().GetRequiredService<IPublicacionService>().ActivarPendientesAsync();

        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(revId));
    }

    [Fact]
    public async Task Cambiar_la_fecha_deja_en_la_bitacora_la_anterior_la_nueva_y_el_motivo()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);
        var nueva = original.AddDays(5);

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, nueva, "Cambio de turno");

        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.ReprogramarVigencia");
        Assert.Equal(f.Admin.Identificador, evento.Usuario);
        Assert.Equal("Cambio de turno", evento.Comentario);
        Assert.Contains(original.ToString("yyyy-MM-dd"), evento.ValorAnterior);
        Assert.Contains(nueva.ToString("yyyy-MM-dd"), evento.ValorNuevo);
    }

    [Theory]
    [InlineData(0)]     // ahora mismo
    [InlineData(-1)]    // pasado
    [InlineData(-400)]
    public async Task La_nueva_fecha_no_puede_ser_ahora_ni_pasada_porque_eso_seria_publicar_ya(int dias)
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(dias), "x"));

        Assert.Contains("posterior", ex.Message);
        Assert.Equal(original, (await e.Revisiones.ObtenerAsync(f.Admin, revId)).FechaVigenciaUtc); // no cambió nada
    }

    [Fact]
    public async Task La_nueva_fecha_tiene_el_mismo_limite_de_un_ano()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);
        var ahora = e.Reloj.GetUtcNow().UtcDateTime;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, ahora.AddDays(400), "x"));
        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, ahora.AddDays(ReglasRevision.MaximoDiasVigenciaDiferida), "justo en el límite");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Cambiar_la_fecha_exige_motivo(string motivo)
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, original.AddDays(1), motivo));
    }

    // ---------- Devolver a borrador ----------

    [Fact]
    public async Task Anular_la_aprobacion_devuelve_la_revision_a_borrador_y_la_vigente_sigue_en_uso()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Se detectó un error en la sección 4");

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Borrador, estados["Rev.01"]);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00"]); // nunca se queda la planta sin procedimiento
        var r = await e.Revisiones.ObtenerAsync(f.Autor, revId);
        Assert.Null(r.FechaAprobacionUtc);
        Assert.Null(r.FechaVigenciaUtc);
        Assert.Null(r.FechaProximaRevisionUtc);
    }

    [Fact]
    public async Task El_envio_anulado_queda_en_el_historial_como_Revocado_con_sus_decisiones_intactas()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Error de contenido");

        var envio = Assert.Single(await e.Flujo.HistorialAsync(f.Autor, revId));
        Assert.Equal(EstadoFlujo.Revocado, envio.Estado);
        Assert.All(envio.Pasos, p => Assert.Equal(EstadoPaso.Aprobado, p.Estado)); // la historia no se reescribe
    }

    [Fact]
    public async Task El_autor_ve_el_motivo_y_que_fue_una_anulacion_no_un_rechazo()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Falta el anexo B");

        var r = await e.Revisiones.ObtenerAsync(f.Autor, revId);
        Assert.True(r.DevueltaPorRevocacion);
        Assert.Equal("Falta el anexo B", r.ComentarioUltimoRechazo);
    }

    [Fact]
    public async Task Tras_anular_el_autor_corrige_reenvia_por_el_flujo_completo_y_se_publica()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);
        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Corregir");

        await e.SubirPdfAsync(f.Autor, revId, "corregido");                 // vuelve a ser editable
        await e.Flujo.IniciarAsync(f.Autor, revId, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        var envio2 = (await e.Flujo.HistorialAsync(f.Autor, revId)).First();
        Assert.Equal(2, envio2.Numero);                                       // envío nuevo, no se reutiliza el anulado
        Assert.Equal(2, envio2.Pasos.Count(p => p.Estado == EstadoPaso.Pendiente)); // vuelve a pasar por TODAS las etapas
        await e.AprobarRevisionAsync(f, revId);

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.01"]);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        var historial = await e.Flujo.HistorialAsync(f.Autor, revId);
        Assert.Equal([EstadoFlujo.Aprobado, EstadoFlujo.Revocado], historial.Select(h => h.Estado));
        Assert.Null((await e.Revisiones.ObtenerAsync(f.Autor, revId)).ComentarioUltimoRechazo); // ya no está devuelta
    }

    [Fact]
    public async Task Una_aprobacion_anulada_nunca_se_publica_aunque_llegue_la_fecha_original()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e, dias: 5);
        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Detenido");

        e.Reloj.Advance(TimeSpan.FromDays(6));
        await e.Scope().GetRequiredService<IPublicacionService>().ActivarPendientesAsync();

        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(revId));
        Assert.Equal(EstadoRevision.Vigente, (await e.EstadosDelDocumentoAsync(f.Documento.Id))["Rev.00"]);
    }

    [Fact]
    public async Task Se_puede_anular_o_reprogramar_aunque_la_fecha_ya_paso_si_el_proceso_aun_no_la_publico()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e, dias: 1);
        e.Reloj.Advance(TimeSpan.FromDays(2)); // la fecha ya pasó, pero TareasDeFondo todavía no corrió

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Llegué antes que el proceso");

        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(revId));
    }

    [Fact]
    public async Task Despues_de_anular_se_puede_descartar_el_borrador_y_abrir_otro_con_el_mismo_numero()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);
        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "No procede");

        await e.Revisiones.CancelarBorradorAsync(f.Autor, revId, "Se descarta");
        var otra = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Nuevo intento", null, false);

        Assert.Equal("Rev.01", otra.Etiqueta);
    }

    [Fact]
    public async Task Anular_la_aprobacion_deja_en_la_bitacora_quien_cuando_y_por_que()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "Contenido desactualizado");

        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.RevocarAprobacion");
        Assert.Equal(f.Admin.Identificador, evento.Usuario);
        Assert.Equal("Contenido desactualizado", evento.Comentario);
        Assert.Contains("Aprobado", evento.ValorAnterior);
        Assert.Contains("Borrador", evento.ValorNuevo);
        Assert.Contains("#1", evento.ValorNuevo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Anular_exige_motivo_y_no_cambia_nada_si_falta(string motivo)
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, motivo));

        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(revId));
    }

    // ---------- Permisos ----------

    [Fact]
    public async Task Solo_el_Administrador_puede_cambiar_la_fecha_o_anular()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        var lector = await e.NuevoActorAsync([]);

        foreach (var quien in new[] { f.Autor, f.Aprobador, f.Revisor, auditor, lector })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ReprogramarVigenciaAsync(quien, revId, original.AddDays(1), "x"));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.RevocarAprobacionAsync(quien, revId, "x"));
        }

        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(revId));
        Assert.Equal(original, (await e.Revisiones.ObtenerAsync(f.Admin, revId)).FechaVigenciaUtc);
    }

    // ---------- Estados en los que NO aplica ----------

    [Fact]
    public async Task No_aplica_a_una_revision_que_ya_es_vigente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, f.RevisionId, "x"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.ReprogramarVigenciaAsync(f.Admin, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(3), "x"));

        Assert.Contains("todavía no entra en vigor", ex.Message);
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId)); // lo vigente no se revierte
    }

    [Fact]
    public async Task No_aplica_a_un_borrador_ni_a_una_revision_en_flujo()
    {
        using var e = new Escenario(bd);
        var borrador = await e.PrepararFlujoAsync();
        var enFlujo = await e.PrepararFlujoAsync();
        await e.IniciarAsync(enFlujo);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(borrador.Admin, borrador.RevisionId, "x"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(enFlujo.Admin, enFlujo.RevisionId, "x"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.ReprogramarVigenciaAsync(enFlujo.Admin, enFlujo.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(3), "x"));
    }

    [Fact]
    public async Task No_se_puede_anular_dos_veces_ni_una_revision_que_no_existe()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e);
        await e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "primera");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "segunda"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, int.MaxValue, "x"));
    }

    [Fact]
    public async Task Si_el_proceso_la_publica_antes_de_que_el_Administrador_actue_se_le_dice_con_claridad()
    {
        using var e = new Escenario(bd);
        var (f, revId, _) = await ProgramadaAsync(e, dias: 1);
        e.Reloj.Advance(TimeSpan.FromDays(2));
        await e.Scope().GetRequiredService<IPublicacionService>().ActivarPendientesAsync(); // se adelantó

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "tarde"));

        Assert.Contains("Vigente", ex.Message);
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(revId));
    }

    [Fact]
    public async Task Un_documento_retirado_no_admite_estas_acciones()
    {
        using var e = new Escenario(bd);
        var (f, revId, original) = await ProgramadaAsync(e);
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja"); // cancela la revisión aprobada

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RevocarAprobacionAsync(f.Admin, revId, "x"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ReprogramarVigenciaAsync(f.Admin, revId, original.AddDays(1), "x"));
    }

    // ---------- Primera emisión programada (sin vigente anterior) ----------

    [Fact]
    public async Task Una_primera_emision_programada_tambien_se_puede_reprogramar_y_anular()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var fecha = e.Reloj.GetUtcNow().UtcDateTime.AddDays(7);
        await e.AprobarRevisionAsync(f, f.RevisionId, fecha);

        await e.Revisiones.ReprogramarVigenciaAsync(f.Admin, f.RevisionId, fecha.AddDays(3), "Se pospone");
        Assert.Equal(fecha.AddDays(3), (await e.Revisiones.ObtenerAsync(f.Admin, f.RevisionId)).FechaVigenciaUtc);

        await e.Revisiones.RevocarAprobacionAsync(f.Admin, f.RevisionId, "Se detiene");

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal(EstadoRevision.Borrador, r.Estado);
        Assert.True(r.EsPrimeraEmision); // sigue siendo la primera emisión: se puede cancelar el documento si se desea
    }

    // ---------- Regresión: el motivo de devolución se toma del ÚLTIMO envío ----------

    [Fact]
    public async Task Un_rechazo_viejo_no_se_muestra_si_el_ultimo_envio_se_cancelo_por_otra_razon()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, false, "Motivo viejo");
        Assert.EndsWith("Motivo viejo", (await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).ComentarioUltimoRechazo);

        await e.SubirPdfAsync(f.Autor, f.RevisionId, "corregido");
        await e.IniciarAsync(f);
        await e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "Me equivoqué de PDF");

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal(EstadoRevision.Borrador, r.Estado);
        Assert.Null(r.ComentarioUltimoRechazo); // el último envío se canceló: el rechazo del primero ya no aplica
        Assert.False(r.DevueltaPorRevocacion);
    }

    [Fact]
    public async Task Un_rechazo_normal_sigue_mostrando_quien_y_por_que_sin_marcarlo_como_anulacion()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, false, "Falta firma");

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);

        Assert.EndsWith("Falta firma", r.ComentarioUltimoRechazo);
        Assert.False(r.DevueltaPorRevocacion);
    }
}
