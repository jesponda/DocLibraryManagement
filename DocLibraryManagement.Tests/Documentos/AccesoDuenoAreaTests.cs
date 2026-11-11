using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Documentos;

/// <summary>
/// El dueño del área ve los documentos de SU área (solo lectura) aunque nunca haya tenido una etapa en ellos: sin eso no
/// podría abrir el documento para decidir un «revisado sin cambios». Verlo no le da permiso de editar.
/// </summary>
public class AccesoDuenoAreaTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    /// <summary>Un documento publicado cuyo dueño de área es OTRA persona que quienes lo aprobaron.</summary>
    private async Task<(Escenario E, EscenarioFlujo F, Actor Dueno)> ConDuenoAjenoAsync()
    {
        var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var dueno = await e.NuevoAprobadorAsync(); // no es f.Aprobador: jamás tuvo una etapa en este documento
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, await e.IdDeAsync(dueno));
        await e.PublicarPrimeraAsync(f);
        return (e, f, dueno);
    }

    [Fact]
    public async Task El_dueno_del_area_abre_el_documento_el_detalle_de_sus_revisiones_y_su_historial_sin_haber_tenido_etapa()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;

        var doc = await e.Documentos.ObtenerAsync(dueno, f.Documento.Id);
        var rev = await e.Revisiones.ObtenerAsync(dueno, f.RevisionId);
        var historial = await e.Flujo.HistorialAsync(dueno, f.RevisionId);

        Assert.Equal(f.Documento.Codigo, doc!.Codigo);
        Assert.Equal("Rev.00", rev.Etiqueta);
        Assert.NotEmpty(historial);
    }

    [Fact]
    public async Task El_dueno_tambien_ve_un_borrador_de_su_area()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        Assert.Equal(EstadoRevision.Borrador, (await e.Revisiones.ObtenerAsync(dueno, borrador.Id)).Estado);
    }

    [Fact]
    public async Task Ver_no_es_editar_el_dueno_no_modifica_ni_crea_revisiones()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ActualizarBorradorAsync(dueno, borrador.Id, "Mi cambio", null));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.SubirPdfAsync(dueno, borrador.Id));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CancelarBorradorAsync(dueno, borrador.Id, "No"));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CambiarSubAreaAsync(dueno, f.Documento.Id, null));
    }

    [Fact]
    public async Task El_dueno_de_otra_area_no_ve_este_documento()
    {
        var (e, f, _) = await ConDuenoAjenoAsync();
        using var _ = e;
        var otraArea = await e.NuevaAreaConTipoAsync(f.Admin);
        var duenoDeOtra = await e.NuevoAprobadorAsync();
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, otraArea.AreaId, await e.IdDeAsync(duenoDeOtra));

        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(duenoDeOtra, f.Documento.Id));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ObtenerAsync(duenoDeOtra, borrador.Id)); // la vigente es de lectura abierta; el borrador no
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Flujo.HistorialAsync(duenoDeOtra, f.RevisionId));
    }

    [Fact]
    public async Task Un_aprobador_que_no_es_dueno_sigue_sin_ver_documentos_ajenos()
    {
        var (e, f, _) = await ConDuenoAjenoAsync();
        using var _ = e;
        var otroAprobador = await e.NuevoAprobadorAsync();

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(otroAprobador, f.Documento.Id));
    }

    [Fact]
    public async Task Si_al_dueno_le_quitan_el_rol_de_aprobador_pierde_el_acceso_aunque_el_area_lo_siga_nombrando()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;
        var sinRol = dueno with { Roles = new HashSet<RolSistema> { RolSistema.Lector } }; // cambiaron sus grupos de AD

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(sinRol, f.Documento.Id));
    }

    [Fact]
    public async Task Un_dueno_ya_no_designado_deja_de_ver_el_area()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId); // el administrador cambia al dueño

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(dueno, f.Documento.Id));
    }

    [Fact]
    public async Task De_punta_a_punta_el_dueno_ajeno_al_flujo_abre_el_documento_y_decide_el_revisado_sin_cambios()
    {
        var (e, f, dueno) = await ConDuenoAjenoAsync();
        using var _ = e;
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 10);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado, sigue igual");

        var pendientes = await e.Vencimientos.PendientesDeDecisionAsync(dueno);              // lo que ve en «Mis tareas»
        Assert.Contains(pendientes, s => s.Id == id);
        Assert.NotNull(await e.Documentos.ObtenerAsync(dueno, pendientes.Single(s => s.Id == id).DocumentoId)); // el enlace «Decidir» abre
        var panel = await e.Vencimientos.PanelAsync(dueno, f.RevisionId);
        Assert.True(panel!.PuedeDecidir);

        await e.Vencimientos.DecidirSinCambiosAsync(dueno, id, true, null);

        Assert.Equal(EstadoSolicitudSinCambios.Aprobada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId)).Estado);
    }
}
