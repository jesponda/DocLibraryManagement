using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Documentos;

public class RetiroYDuenoTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    // ---------- Retiro ----------

    [Fact]
    public async Task Retirar_pasa_la_vigente_a_Obsoleta_cancela_lo_abierto_y_el_flujo_en_curso()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, borrador.Id); // flujo en curso con tareas pendientes
        Assert.Single(await e.Flujo.MisTareasAsync(f.Revisor));

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "El proceso se descontinuó");

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Cancelado, estados["Rev.01"]);
        var flujo = (await e.Flujo.HistorialAsync(f.Admin, borrador.Id)).Single();
        Assert.Equal(EstadoFlujo.Cancelado, flujo.Estado);
        Assert.All(flujo.Pasos, p => Assert.Equal(EstadoPaso.Omitido, p.Estado));
        Assert.Empty(await e.Flujo.MisTareasAsync(f.Revisor)); // no quedan tareas huérfanas

        var doc = await e.Documentos.ObtenerAsync(f.Admin, f.Documento.Id);
        Assert.Equal(EstadoDocumento.Retirado, doc!.Estado);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Documento.Retirar");
        Assert.Equal("El proceso se descontinuó", evento.Comentario);
        Assert.Contains("Rev.00 Vigente→Obsoleto", evento.ValorNuevo);
        Assert.Contains("Rev.01", evento.ValorNuevo);
    }

    [Fact]
    public async Task Solo_el_Administrador_retira_y_con_motivo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.RetirarDocumentoAsync(f.Autor, f.Documento.Id, "baja"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.RetirarDocumentoAsync(f.Aprobador, f.Documento.Id, "baja"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "  "));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RetirarDocumentoAsync(f.Admin, int.MaxValue, "baja"));

        Assert.Equal(EstadoRevision.Vigente, await e.EstadoRevisionAsync(f.RevisionId)); // los intentos fallidos no cambiaron nada
    }

    [Fact]
    public async Task Un_documento_retirado_no_admite_mas_cambios_ni_se_retira_dos_veces()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "otra vez"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "x", null, false));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "x"));
    }

    [Fact]
    public async Task Retirar_cancela_tambien_una_revision_aprobada_con_vigencia_futura_y_nunca_se_activa()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        await e.EnviarRevisionAsync(f, nueva.Id);
        await e.AprobarRevisionAsync(f, nueva.Id, e.Reloj.GetUtcNow().UtcDateTime.AddDays(5));
        Assert.Equal(EstadoRevision.Aprobado, await e.EstadoRevisionAsync(nueva.Id));

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja");
        e.Reloj.Advance(TimeSpan.FromDays(6));
        await e.Scope().GetRequiredService<IPublicacionService>().ActivarPendientesAsync();

        Assert.Equal(EstadoRevision.Cancelado, await e.EstadoRevisionAsync(nueva.Id));
        Assert.Equal(EstadoRevision.Obsoleto, await e.EstadoRevisionAsync(f.RevisionId));
    }

    [Fact]
    public async Task Retirar_un_documento_cuya_primera_revision_nunca_se_publico_la_cancela()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "no se usará");

        Assert.Equal(EstadoRevision.Cancelado, await e.EstadoRevisionAsync(f.RevisionId));
    }

    [Fact]
    public async Task El_PDF_de_un_documento_retirado_sigue_siendo_consultable_por_los_gestores_para_auditoria()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja");
        var lector = await e.NuevoActorAsync([]);

        await (await e.Revisiones.AbrirArchivoAsync(f.Admin, f.RevisionId, DocLibraryManagement.Application.Archivos.TipoArchivo.Pdf)).Contenido.DisposeAsync();
        await Assert.ThrowsAsync<AccesoDenegadoException>(() =>
            e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, DocLibraryManagement.Application.Archivos.TipoArchivo.Pdf));
    }

    // ---------- Dueño de área ----------

    [Fact]
    public async Task El_Administrador_designa_al_dueno_y_queda_visible_y_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);

        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == f.AreaId);
        Assert.Equal(f.AprobadorId, area.DuenoId);
        Assert.False(string.IsNullOrEmpty(area.NombreDueno));
        var evento = (await e.AuditoriaDeAsync(f.CodigoArea)).Single(x => x.Accion == "Area.Dueno");
        Assert.Equal("(sin dueño)", evento.ValorAnterior);
    }

    [Fact]
    public async Task Se_puede_cambiar_y_quitar_al_dueno()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var otro = await e.NuevoAprobadorAsync();
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);

        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, await e.IdDeAsync(otro));
        Assert.Equal(await e.IdDeAsync(otro), Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == f.AreaId).DuenoId);

        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, null);
        Assert.Null(Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == f.AreaId).DuenoId);
    }

    [Fact]
    public async Task El_dueno_debe_tener_el_rol_Aprobador_existir_y_estar_activo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.RevisorId)); // revisor
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, Guid.NewGuid()));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, int.MaxValue, f.AprobadorId));
    }

    [Theory]
    [InlineData(RolSistema.Autor)]
    [InlineData(RolSistema.Aprobador)]
    [InlineData(RolSistema.Auditor)]
    public async Task Solo_el_Administrador_designa_dueños(RolSistema rol)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var actor = await e.NuevoActorAsync([rol], f.CodigoArea);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Catalogo.EstablecerDuenoDeAreaAsync(actor, f.AreaId, f.AprobadorId));
    }

    [Fact]
    public async Task Un_area_sin_dueno_lo_muestra_como_vacio()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var area = Assert.Single(await e.Catalogo.ListarAreasAsync(), a => a.Id == f.AreaId);

        Assert.Null(area.DuenoId);
        Assert.Null(area.NombreDueno);
    }
}
