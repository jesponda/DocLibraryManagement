using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Documentos;

public class RevisionesNuevasTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static async Task<EscenarioFlujo> PublicadoAsync(Escenario e, bool conDueno = true)
    {
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        if (conDueno)
        {
            await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        }

        return f;
    }

    // ---------- Crear ----------

    [Fact]
    public async Task Una_revision_mayor_nace_como_Rev01_en_borrador_a_partir_de_la_vigente()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);

        var nueva = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "  Se actualiza el límite  ", "Detalle", false);

        Assert.Equal("Rev.01", nueva.Etiqueta);
        Assert.Equal(EstadoRevision.Borrador, nueva.Estado);
        Assert.Equal("Se actualiza el límite", nueva.MotivoCambio);
        Assert.Equal("Detalle", nueva.DescripcionCambio);
        Assert.False(nueva.TienePdf);                       // los archivos no se heredan: se sube el PDF nuevo
        Assert.False(nueva.EsPrimeraEmision);
        Assert.False(nueva.EsMenor);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.Crear");
        Assert.Equal("Rev.00", evento.ValorAnterior);
        Assert.Contains("Rev.01", evento.ValorNuevo);
    }

    [Fact]
    public async Task La_vigente_sigue_intacta_mientras_existe_el_borrador()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);

        await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Borrador, estados["Rev.01"]);
    }

    [Fact]
    public async Task Solo_puede_haber_una_revision_abierta_por_documento()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Otra", null, false));
        Assert.Contains("en curso", ex.Message);
    }

    [Fact]
    public async Task Creaciones_simultaneas_dejan_una_sola_revision_abierta()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);

        var intentos = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            using var otro = new Escenario(bd);
            try
            {
                await otro.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
                return true;
            }
            catch (ReglaNegocioException)
            {
                return false; // los perdedores reciben un mensaje de negocio, no un error técnico
            }
        })).ToArray();

        Assert.Equal(1, (await Task.WhenAll(intentos)).Count(r => r));
        await using var db = e.Db();
        Assert.Equal(1, await db.Revisiones.CountAsync(r => r.DocumentoId == f.Documento.Id && r.Estado == EstadoRevision.Borrador));
    }

    [Fact]
    public async Task Sin_revision_vigente_no_se_puede_abrir_una_nueva()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        // La única revisión sigue en borrador: lo que corresponde es editarla, no crear otra.
        await e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "x");
        var g = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.CrearRevisionAsync(g.Autor, g.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false));
    }

    [Fact]
    public async Task Exige_motivo_permiso_y_documento_activo()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var ajeno = await e.NuevoAutorAsync("OTRA");
        var lector = await e.NuevoActorAsync([]);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "  ", null, false));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, new string('x', 1001), null, false));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CrearRevisionAsync(ajeno, f.Documento.Id, TipoRevisionNueva.Mayor, "x", null, false));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CrearRevisionAsync(lector, f.Documento.Id, TipoRevisionNueva.Mayor, "x", null, false));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CrearRevisionAsync(f.Revisor, f.Documento.Id, TipoRevisionNueva.Mayor, "x", null, false));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, (TipoRevisionNueva)9, "x", null, false));

        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "baja");
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "x", null, false));
    }

    // ---------- Revisión menor ----------

    [Fact]
    public async Task Una_menor_exige_declarar_que_no_altera_requisitos_y_que_el_area_tenga_dueno()
    {
        using var e = new Escenario(bd);
        var sinDueno = await PublicadoAsync(e, conDueno: false);
        var conDueno = await PublicadoAsync(e);

        var ex1 = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.CrearRevisionAsync(conDueno.Autor, conDueno.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, declaraQueNoAlteraRequisitos: false));
        var ex2 = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.CrearRevisionAsync(sinDueno.Autor, sinDueno.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, true));

        Assert.Contains("no altera requisitos", ex1.Message);
        Assert.Contains("dueño", ex2.Message);
    }

    [Fact]
    public async Task Una_menor_lleva_numeracion_propia_y_deja_la_declaracion_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);

        var menor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Corrección ortográfica", null, true);

        Assert.Equal("Rev.00.1", menor.Etiqueta);
        Assert.True(menor.EsMenor);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.Crear");
        Assert.Contains("no altera requisitos", evento.Comentario);
    }

    [Fact]
    public async Task La_menor_se_envia_sola_al_dueno_con_una_unica_aprobacion_y_publica()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var menor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, true);
        await e.SubirPdfAsync(f.Autor, menor.Id, "corregido");

        // No se pasan asignaciones: el dueño del área es el aprobador fijo.
        await e.Flujo.IniciarAsync(f.Autor, menor.Id, []);

        Assert.Equal(EstadoRevision.EnAprobacion, await e.EstadoRevisionAsync(menor.Id));
        var flujo = (await e.Flujo.HistorialAsync(f.Autor, menor.Id)).Single();
        var paso = Assert.Single(flujo.Pasos);
        Assert.Equal(TipoPaso.Aprobacion, paso.Clase);
        Assert.Equal(f.AprobadorId, paso.AsignadoAId);
        Assert.Contains("menor", paso.Nombre);
        Assert.Single(await e.Flujo.MisTareasAsync(f.Aprobador));

        await e.Flujo.DecidirAsync(f.Aprobador, paso.Id, true, null);

        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00.1"]);
    }

    [Fact]
    public async Task La_menor_ignora_las_asignaciones_que_se_le_pasen_y_la_plantilla_del_tipo()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var otroAprobador = await e.NuevoAprobadorAsync();
        var menor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, true);
        await e.SubirPdfAsync(f.Autor, menor.Id, "corregido");

        await e.Flujo.IniciarAsync(f.Autor, menor.Id,
            [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, await e.IdDeAsync(otroAprobador))]);

        var flujo = (await e.Flujo.HistorialAsync(f.Autor, menor.Id)).Single();
        Assert.Single(flujo.Pasos);                         // no hay etapa de revisión aunque el tipo la tenga
        Assert.Equal(f.AprobadorId, flujo.Pasos[0].AsignadoAId); // y el aprobador es el dueño, no el que se pasó
    }

    [Fact]
    public async Task El_dueno_no_puede_aprobar_su_propia_menor()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        // Un autor que además es Aprobador, designado dueño del área: el caso a vigilar.
        var autorDueno = await e.NuevoActorAsync([RolSistema.Autor, RolSistema.Aprobador], f.CodigoArea);
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, await e.IdDeAsync(autorDueno));
        var doc = await e.Documentos.CrearAsync(autorDueno, new("Del dueño", f.AreaId, null, f.TipoId));
        await e.SubirPdfAsync(autorDueno, doc.Revisiones.Single().Id);
        await e.Flujo.IniciarAsync(autorDueno, doc.Revisiones.Single().Id, [new(1, f.RevisorId), new(2, f.AprobadorId)]);
        var flujo = (await e.Flujo.HistorialAsync(autorDueno, doc.Revisiones.Single().Id)).Single();
        await e.Flujo.DecidirAsync(f.Revisor, flujo.Pasos[0].Id, true, null);
        await e.Flujo.DecidirAsync(f.Aprobador, flujo.Pasos[1].Id, true, null);
        var menor = await e.Revisiones.CrearRevisionAsync(autorDueno, doc.Id, TipoRevisionNueva.Menor, "Typo", null, true);
        await e.SubirPdfAsync(autorDueno, menor.Id, "corregido");

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.IniciarAsync(autorDueno, menor.Id, []));

        Assert.Contains("separación de funciones", ex.Message);
        Assert.Equal(EstadoRevision.Borrador, await e.EstadoRevisionAsync(menor.Id));
    }

    [Fact]
    public async Task Si_el_dueno_pierde_el_rol_la_menor_no_se_puede_enviar()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var menor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, true);
        await e.SubirPdfAsync(f.Autor, menor.Id, "corregido");

        await e.Usuarios.SincronizarAsync(f.Aprobador.Identificador, "Ya no aprueba", null, [RolSistema.Lector], []); // su copia de roles cambia

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Flujo.IniciarAsync(f.Autor, menor.Id, []));
        Assert.Contains("Aprobador", ex.Message);
    }

    [Fact]
    public async Task El_plan_de_envio_distingue_mayor_y_menor()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var menor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo", null, true);

        var planMenor = await e.Flujo.ObtenerPlanEnvioAsync(menor.Id);
        await e.Revisiones.CancelarBorradorAsync(f.Autor, menor.Id, "mejor una mayor");
        var mayor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        var planMayor = await e.Flujo.ObtenerPlanEnvioAsync(mayor.Id);

        Assert.True(planMenor.EsMenor);
        Assert.Single(planMenor.Pasos);
        Assert.Equal(f.AprobadorId, planMenor.DuenoId);
        Assert.False(planMayor.EsMenor);
        Assert.Equal(2, planMayor.Pasos.Count);
        Assert.Null(planMayor.DuenoId);
    }

    [Fact]
    public async Task La_numeracion_avanza_menor_menor_mayor()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);

        var m1 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo 1", null, true);
        await e.SubirPdfAsync(f.Autor, m1.Id, "m1");
        await e.Flujo.IniciarAsync(f.Autor, m1.Id, []);
        await e.Flujo.DecidirAsync(f.Aprobador, (await e.Flujo.HistorialAsync(f.Autor, m1.Id)).Single().Pasos[0].Id, true, null);

        var m2 = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Menor, "Typo 2", null, true);
        await e.SubirPdfAsync(f.Autor, m2.Id, "m2");
        await e.Flujo.IniciarAsync(f.Autor, m2.Id, []);
        await e.Flujo.DecidirAsync(f.Aprobador, (await e.Flujo.HistorialAsync(f.Autor, m2.Id)).Single().Pasos[0].Id, true, null);

        var mayor = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio de fondo", null, false);

        Assert.Equal("Rev.00.1", m1.Etiqueta);
        Assert.Equal("Rev.00.2", m2.Etiqueta);
        Assert.Equal("Rev.01", mayor.Etiqueta);
        var estados = await e.EstadosDelDocumentoAsync(f.Documento.Id);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00"]);
        Assert.Equal(EstadoRevision.Obsoleto, estados["Rev.00.1"]);
        Assert.Equal(EstadoRevision.Vigente, estados["Rev.00.2"]);
    }

    // ---------- Descartar borradores ----------

    [Fact]
    public async Task Un_borrador_cancelado_no_consume_el_numero_de_revision()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var primero = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Primer intento", null, false);

        await e.Revisiones.CancelarBorradorAsync(f.Autor, primero.Id, "Cambio de planes");
        var segundo = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Segundo intento", null, false);

        Assert.Equal("Rev.01", primero.Etiqueta);
        Assert.Equal("Rev.01", segundo.Etiqueta); // el número vuelve a estar disponible
        var estados = await e.Db().Revisiones.Where(r => r.DocumentoId == f.Documento.Id && r.Mayor == 1).Select(r => r.Estado).ToListAsync();
        Assert.Contains(EstadoRevision.Cancelado, estados);
        Assert.Contains(EstadoRevision.Borrador, estados);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.CancelarBorrador");
        Assert.Equal("Cambio de planes", evento.Comentario);
    }

    [Fact]
    public async Task Descartar_un_borrador_deja_la_vigente_y_permite_otro_borrador()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);

        await e.Revisiones.CancelarBorradorAsync(f.Autor, borrador.Id, "No procede");

        Assert.Equal(EstadoRevision.Vigente, (await e.EstadosDelDocumentoAsync(f.Documento.Id))["Rev.00"]);
        Assert.Equal(EstadoRevision.Cancelado, await e.EstadoRevisionAsync(borrador.Id));
    }

    [Fact]
    public async Task La_primera_emision_se_descarta_cancelando_el_documento_no_con_este_metodo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarBorradorAsync(f.Autor, f.RevisionId, "x"));
        Assert.Contains("Cancelar este documento", ex.Message);
    }

    [Fact]
    public async Task Solo_se_descarta_un_borrador_y_quien_edita_en_el_area_con_motivo()
    {
        using var e = new Escenario(bd);
        var f = await PublicadoAsync(e);
        var borrador = await e.Revisiones.CrearRevisionAsync(f.Autor, f.Documento.Id, TipoRevisionNueva.Mayor, "Cambio", null, false);
        var ajeno = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarBorradorAsync(f.Autor, borrador.Id, " "));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CancelarBorradorAsync(ajeno, borrador.Id, "ajeno"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarBorradorAsync(f.Autor, f.RevisionId, "es la vigente"));

        await e.EnviarRevisionAsync(f, borrador.Id);
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarBorradorAsync(f.Autor, borrador.Id, "ya en flujo"));
    }
}
