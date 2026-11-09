using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Documentos;

public class PublicacionTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    // ---------- Primera publicación ----------

    [Fact]
    public async Task La_ultima_aprobacion_publica_la_revision_y_calcula_la_proxima_revision_periodica()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.PublicarPrimeraAsync(f);

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        var ahora = e.Reloj.GetUtcNow().UtcDateTime;
        Assert.Equal(EstadoRevision.Vigente, r.Estado);
        Assert.Equal(ahora, r.FechaVigenciaUtc);
        Assert.Equal(ahora.AddMonths(12), r.FechaProximaRevisionUtc); // el tipo de prueba se revisa cada 12 meses
        Assert.Equal(ahora, r.FechaAprobacionUtc);

        var bitacora = await e.AuditoriaDeAsync(f.Documento.Codigo);
        var publicar = Assert.Single(bitacora, x => x.Accion == "Revision.Publicar");
        Assert.Equal(f.Aprobador.Identificador, publicar.Usuario);
        Assert.DoesNotContain(bitacora, x => x.Accion == "Revision.Obsoletar"); // no había anterior
    }

    [Fact]
    public async Task Los_meses_de_revision_se_toman_del_tipo_del_documento()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Catalogo.ActualizarTipoAsync(f.Admin, f.TipoId, "Tipo semestral", mesesRevision: 6, activo: true);

        await e.PublicarPrimeraAsync(f);

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal(r.FechaVigenciaUtc!.Value.AddMonths(6), r.FechaProximaRevisionUtc);
    }

    [Fact]
    public async Task Una_revision_vigente_ya_no_se_puede_editar_ni_reemplazar_sus_archivos()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, "cambio", null));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.SubirPdfAsync(f.Autor, f.RevisionId, "otro"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.CancelarFlujoAsync(f.Autor, f.RevisionId, "ya publicada"));
    }

    [Fact]
    public async Task Lo_vigente_es_de_lectura_abierta_para_cualquier_usuario()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var lector = await e.NuevoActorAsync([]);
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf)); // aún borrador

        await e.PublicarPrimeraAsync(f);

        await (await e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync();
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Fuente));
    }

    // ---------- Reemplazo: la anterior queda obsoleta ----------

    [Fact]
    public async Task Publicar_una_revision_nueva_deja_la_anterior_Obsoleta_en_la_misma_operacion()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Actualización anual", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);

        await e.AprobarRevisionAsync(f, nueva.Id);

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.01"]);
        await using var db = e.Db();
        Assert.Equal(1, await db.Revisiones.CountAsync(r => r.DocumentoId == f.Documento.Id && r.Estado == EstadoRevision.Vigente));

        var bitacora = await e.AuditoriaDeAsync(f.Documento.Codigo);
        var obsoletar = Assert.Single(bitacora, x => x.Accion == "Revision.Obsoletar");
        Assert.Contains("Rev.01", obsoletar.Comentario);
    }

    [Fact]
    public async Task La_revision_obsoleta_conserva_sus_archivos_intactos_y_solo_la_ven_los_gestores()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var hashOriginal = (await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).HashPdf;
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id, "contenido distinto");
        await e.AprobarRevisionAsync(f, nueva.Id);
        var lector = await e.NuevoActorAsync([]);

        var obsoleta = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal(EstadoRevision.Obsoleto, obsoleta.Estado);
        Assert.Equal(hashOriginal, obsoleta.HashPdf);
        await (await e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync(); // la integridad se verifica
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ObtenerAsync(lector, f.RevisionId));
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Revision.DescargarObsoleta");
    }

    [Fact]
    public async Task Un_rechazo_de_la_revision_nueva_deja_intacta_a_la_vigente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);

        var paso = (await e.Flujo.HistorialAsync(f.Autor, nueva.Id)).First().Pasos[0];
        await e.Flujo.DecidirAsync(f.Revisor, paso.Id, false, "No procede");

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00"]); // sigue en uso: la planta nunca se queda sin procedimiento
        Assert.Equal(EstadoRevision.Borrador, estados["Rev.01"]);
    }

    // ---------- Vigencia diferida ----------

    [Fact]
    public async Task Con_fecha_futura_la_revision_queda_Aprobada_y_la_vigente_sigue_en_uso()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        var fecha = e.Reloj.GetUtcNow().UtcDateTime.AddDays(10);

        await e.AprobarRevisionAsync(f, nueva.Id, fecha);

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Aprobado, estados["Rev.01"]);
        var r = await e.Revisiones.ObtenerAsync(f.Autor, nueva.Id);
        Assert.Equal(fecha, r.FechaVigenciaUtc);
        Assert.Null(r.FechaProximaRevisionUtc); // se calcula al entrar en vigor
        Assert.Empty(await e.Flujo.MisTareasAsync(f.Aprobador));
    }

    [Fact]
    public async Task Al_llegar_la_fecha_se_publica_con_la_fecha_programada_y_firma_el_sistema()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        var programada = e.Reloj.GetUtcNow().UtcDateTime.AddDays(10);
        await e.AprobarRevisionAsync(f, nueva.Id, programada);
        var publicacion = e.Scope().GetRequiredService<IPublicacionService>();

        await publicacion.ActivarPendientesAsync();
        Assert.Equal(EstadoRevision.Aprobado, (await e.EstadosDelDocumentoAsync(f.Documento.Id))["Rev.01"]); // todavía no es la fecha

        e.Reloj.Advance(TimeSpan.FromDays(10) + TimeSpan.FromHours(3)); // el proceso llega tarde
        var publicadas = await publicacion.ActivarPendientesAsync();

        Assert.True(publicadas >= 1);
        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.01"]);
        var r = await e.Revisiones.ObtenerAsync(f.Autor, nueva.Id);
        Assert.Equal(programada, r.FechaVigenciaUtc);                    // lo prometido, no la hora real del proceso
        Assert.Equal(programada.AddMonths(12), r.FechaProximaRevisionUtc);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Where(x => x.Accion == "Revision.Publicar").Last();
        Assert.Equal(Actor.IdentificadorSistema, evento.Usuario);
    }

    [Fact]
    public async Task Activar_pendientes_es_idempotente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(2));
        var publicacion = e.Scope().GetRequiredService<IPublicacionService>();
        e.Reloj.Advance(TimeSpan.FromDays(3));

        var primera = await publicacion.ActivarPendientesAsync();
        var segunda = await publicacion.ActivarPendientesAsync();

        Assert.True(primera >= 1);
        Assert.Equal(0, segunda);
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId));
        Assert.Single(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Revision.Publicar");
    }

    [Fact]
    public async Task Varias_revisiones_vencidas_se_publican_cada_una_en_lo_suyo()
    {
        using var e = new Escenario(bd);
        var uno = await e.PrepararFlujoAsync();
        var dos = await e.PrepararFlujoAsync();
        var fecha = e.Reloj.GetUtcNow().UtcDateTime.AddDays(1);
        await e.IniciarAsync(uno);
        await e.AprobarRevisionAsync(uno, uno.RevisionId, fecha);
        await e.IniciarAsync(dos);
        await e.AprobarRevisionAsync(dos, dos.RevisionId, fecha.AddDays(1));
        var publicacion = e.Scope().GetRequiredService<IPublicacionService>();

        e.Reloj.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await publicacion.ActivarPendientesAsync();
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(uno.RevisionId));
        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(dos.RevisionId)); // la suya aún no vence

        e.Reloj.Advance(TimeSpan.FromDays(1));
        await publicacion.ActivarPendientesAsync();
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(dos.RevisionId));
    }

    [Fact]
    public async Task Una_fecha_de_mas_de_un_ano_se_rechaza_sin_cambiar_nada()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;
        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null);

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Flujo.DecidirAsync(f.Aprobador, pasos[1].Id, true, null, e.Reloj.GetUtcNow().UtcDateTime.AddDays(400)));

        Assert.Equal(EstadoRevision.EnAprobacion, await e.EstadoRevisionAsync(f.RevisionId));
        Assert.Equal(EstadoPaso.Pendiente, (await e.UltimoFlujoAsync(f)).Pasos[1].Estado);
        Assert.Single(await e.Flujo.MisTareasAsync(f.Aprobador)); // puede volver a intentarlo con una fecha válida
    }

    [Fact]
    public async Task Una_fecha_pasada_en_la_ultima_aprobacion_publica_de_inmediato()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.AprobarRevisionAsync(f, f.RevisionId, e.Reloj.GetUtcNow().UtcDateTime.AddDays(-5));

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal(EstadoRevision.Vigente, r.Estado);
        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime, r.FechaVigenciaUtc);
    }

    [Fact]
    public async Task La_fecha_se_ignora_en_etapas_intermedias_y_al_rechazar()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var pasos = (await e.UltimoFlujoAsync(f)).Pasos;
        var absurda = e.Reloj.GetUtcNow().UtcDateTime.AddYears(50);

        await e.Flujo.DecidirAsync(f.Revisor, pasos[0].Id, true, null, absurda);   // intermedia: no valida
        await e.Flujo.DecidirAsync(f.Aprobador, pasos[1].Id, false, "No", absurda); // rechazo: no valida

        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(f.RevisionId));
    }

    // ---------- Garantía de la base de datos ----------

    [Fact]
    public async Task Dos_publicaciones_simultaneas_de_documentos_distintos_no_se_estorban()
    {
        using var e = new Escenario(bd);
        var a = await e.PrepararFlujoAsync();
        var b = await e.PrepararFlujoAsync();
        await e.IniciarAsync(a);
        await e.IniciarAsync(b);

        await Task.WhenAll(
            Task.Run(async () => { using var x = new Escenario(bd); await x.AprobarRevisionAsync(a, a.RevisionId); }),
            Task.Run(async () => { using var y = new Escenario(bd); await y.AprobarRevisionAsync(b, b.RevisionId); }));

        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(a.RevisionId));
        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(b.RevisionId));
    }
}
