using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Documentos;

/// <summary>El Administrador cambia el responsable de un documento (p. ej. porque la persona dejó la planta).</summary>
public class CambiarResponsableTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private const string Motivo = "La responsable anterior cambió de puesto";

    private async Task<(Escenario E, EscenarioFlujo F, Actor Nuevo, Guid NuevoId)> ListoAsync()
    {
        var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        var nuevo = await e.NuevoAutorAsync(f.CodigoArea);
        return (e, f, nuevo, await e.IdDeAsync(nuevo));
    }

    private static async Task<string> ResponsableAsync(Escenario e, EscenarioFlujo f) =>
        (await e.Documentos.ObtenerAsync(f.Admin, f.Documento.Id))!.Responsable;

    // ---------- Cambio ----------

    [Fact]
    public async Task El_administrador_cambia_el_responsable_y_queda_en_la_bitacora_con_el_motivo()
    {
        var (e, f, nuevo, nuevoId) = await ListoAsync();
        using var _ = e;
        var anteriorNombre = await ResponsableAsync(e, f);

        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, "  " + Motivo + "  ");

        var ahora = await ResponsableAsync(e, f);
        Assert.NotEqual(anteriorNombre, ahora);
        Assert.Equal($"Usuario {nuevo.Identificador[5..11]}", ahora);
        var evento = Assert.Single(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Documento.CambiarResponsable");
        Assert.Equal(f.Admin.Identificador, evento.Usuario);
        Assert.Contains(f.Autor.Identificador, evento.ValorAnterior);
        Assert.Contains(nuevo.Identificador, evento.ValorNuevo);
        Assert.Equal(Motivo, evento.Comentario);                // sin los espacios sobrantes
    }

    [Fact]
    public async Task Avisa_a_la_persona_nueva_y_a_la_anterior_con_el_motivo()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;

        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo);

        var avisos = await e.NotificacionesDeAsync(f.Documento.Codigo);
        var asignado = Assert.Single(avisos, n => n.Tipo == EventosCorreo.ResponsableAsignado);
        var retirado = Assert.Single(avisos, n => n.Tipo == EventosCorreo.ResponsableRetirado);
        Assert.Equal(nuevoId, asignado.DestinatarioId);
        Assert.Equal(await e.IdDeAsync(f.Autor), retirado.DestinatarioId);
        Assert.Contains(Motivo, asignado.Cuerpo);
        Assert.Contains(Motivo, retirado.Cuerpo);
        Assert.Contains("Nuevo responsable: Usuario", retirado.Cuerpo);
        Assert.Contains($"https://docs.test/documentos/{f.Documento.Id}", asignado.Cuerpo);
    }

    [Fact]
    public async Task Si_el_administrador_se_asigna_a_si_mismo_no_se_avisa_a_si_mismo()
    {
        var (e, f, _, _) = await ListoAsync();
        using var _ = e;

        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, await e.IdDeAsync(f.Admin), Motivo);

        var avisos = await e.NotificacionesDeAsync(f.Documento.Codigo);
        Assert.DoesNotContain(avisos, n => n.Tipo == EventosCorreo.ResponsableAsignado);
        Assert.Contains(avisos, n => n.Tipo == EventosCorreo.ResponsableRetirado);
    }

    [Fact]
    public async Task Los_avisos_de_vencimiento_pasan_a_la_persona_nueva()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;
        await e.PublicarPrimeraAsync(f);
        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo);
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, -1);

        await e.Vencimientos.ProcesarAsync();

        var vencido = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo), n => n.Tipo == EventosCorreo.DocumentoVencido);
        Assert.Equal(nuevoId, vencido.DestinatarioId);
    }

    [Fact]
    public async Task Solo_el_responsable_nuevo_puede_proponer_sin_cambios()
    {
        var (e, f, nuevo, nuevoId) = await ListoAsync();
        using var _ = e;
        await e.PublicarPrimeraAsync(f);
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 10);
        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Soy el anterior"));
        Assert.True(await e.Vencimientos.ProponerSinCambiosAsync(nuevo, f.RevisionId, "Soy el nuevo") > 0);
    }

    [Fact]
    public async Task Una_solicitud_pendiente_de_la_persona_anterior_sigue_su_curso()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        await e.PublicarPrimeraAsync(f);
        await e.IrASiguienteRevisionAsync(f, f.RevisionId, 10);
        var id = await e.Vencimientos.ProponerSinCambiosAsync(f.Autor, f.RevisionId, "Revisado");

        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo);
        await e.Vencimientos.DecidirSinCambiosAsync(f.Aprobador, id, true, null);

        Assert.Equal(EstadoSolicitudSinCambios.Aprobada, Assert.Single(await e.Vencimientos.HistorialAsync(f.Admin, f.RevisionId)).Estado);
    }

    // ---------- Candidatos ----------

    [Fact]
    public async Task Los_candidatos_son_autores_del_area_y_administradores_activos_sin_el_responsable_actual()
    {
        var (e, f, nuevo, nuevoId) = await ListoAsync();
        using var _ = e;
        var autorOtraArea = await e.NuevoAutorAsync(Escenario.CodigoUnico("Z"));
        var otroAdmin = await e.NuevoAdminAsync();
        var inactivo = await e.NuevoAutorAsync(f.CodigoArea);
        await using (var db = e.Db())
        {
            var idInactivo = await e.IdDeAsync(inactivo);
            await db.Usuarios.Where(u => u.Id == idInactivo).ExecuteUpdateAsync(s => s.SetProperty(u => u.Activo, false));
        }

        var ids = (await e.Documentos.CandidatosResponsableAsync(f.Admin, f.Documento.Id)).Select(c => c.Id).ToHashSet();

        Assert.Contains(nuevoId, ids);                                   // autor del área
        Assert.Contains(await e.IdDeAsync(otroAdmin), ids);              // administrador
        Assert.DoesNotContain(await e.IdDeAsync(f.Autor), ids);          // el responsable actual
        Assert.DoesNotContain(await e.IdDeAsync(autorOtraArea), ids);    // autor de otra área
        Assert.DoesNotContain(f.RevisorId, ids);                         // revisor
        Assert.DoesNotContain(f.AprobadorId, ids);                       // aprobador
        Assert.DoesNotContain(await e.IdDeAsync(inactivo), ids);         // desactivado
    }

    [Fact]
    public async Task Los_candidatos_salen_ordenados_por_nombre_con_su_identificador()
    {
        var (e, f, nuevo, _) = await ListoAsync();
        using var _ = e;
        await e.NuevoAutorAsync(f.CodigoArea);

        var candidatos = await e.Documentos.CandidatosResponsableAsync(f.Admin, f.Documento.Id);

        Assert.Equal(candidatos.Select(c => c.NombreVisible).OrderBy(n => n), candidatos.Select(c => c.NombreVisible));
        Assert.Contains(candidatos, c => c.Identificador == nuevo.Identificador);
    }

    // ---------- Validaciones ----------

    [Fact]
    public async Task Solo_el_administrador_puede_cambiar_el_responsable_ni_siquiera_el_dueno_ni_el_auditor()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;
        await e.Catalogo.EstablecerDuenoDeAreaAsync(f.Admin, f.AreaId, f.AprobadorId);
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var actor in new[] { f.Autor, f.Aprobador, f.Revisor, auditor })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CambiarResponsableAsync(actor, f.Documento.Id, nuevoId, Motivo));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CandidatosResponsableAsync(actor, f.Documento.Id));
        }

        Assert.StartsWith("Usuario ", await ResponsableAsync(e, f));
        Assert.DoesNotContain(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Documento.CambiarResponsable");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task El_motivo_es_obligatorio(string motivo)
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, motivo));
    }

    [Fact]
    public async Task El_motivo_tiene_un_maximo()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, new string('x', 1001)));
        await e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, new string('x', 1000));
    }

    [Fact]
    public async Task No_se_puede_elegir_al_mismo_responsable()
    {
        var (e, f, _, _) = await ListoAsync();
        using var _ = e;

        var idActual = await e.IdDeAsync(f.Autor);
        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, idActual, Motivo));

        Assert.Contains("ya es", ex.Message);
    }

    [Fact]
    public async Task No_se_puede_elegir_a_quien_no_es_autor_del_area_ni_administrador()
    {
        var (e, f, _, _) = await ListoAsync();
        using var _ = e;
        var autorOtraArea = await e.NuevoAutorAsync(Escenario.CodigoUnico("Z"));
        var antes = await ResponsableAsync(e, f);

        foreach (var id in new[] { await e.IdDeAsync(autorOtraArea), f.RevisorId, f.AprobadorId, Guid.NewGuid() })
        {
            await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, id, Motivo));
        }

        Assert.Equal(antes, await ResponsableAsync(e, f)); // ningún intento fallido lo cambió
    }

    [Fact]
    public async Task No_se_puede_elegir_a_una_persona_desactivada()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;
        await using (var db = e.Db())
        {
            await db.Usuarios.Where(u => u.Id == nuevoId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Activo, false));
        }

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo));
    }

    [Fact]
    public async Task Un_documento_retirado_o_inexistente_no_cambia_de_responsable()
    {
        var (e, f, _, nuevoId) = await ListoAsync();
        using var _ = e;
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, nuevoId, Motivo));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, int.MaxValue, nuevoId, Motivo));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CandidatosResponsableAsync(f.Admin, int.MaxValue));
    }

    [Fact]
    public async Task Una_validacion_fallida_no_deja_avisos_ni_bitacora()
    {
        var (e, f, _, _) = await ListoAsync();
        using var _ = e;
        var antes = (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarResponsableAsync(f.Admin, f.Documento.Id, f.RevisorId, Motivo));

        Assert.Equal(antes, (await e.NotificacionesDeAsync(f.Documento.Codigo)).Count);
        Assert.DoesNotContain(await e.AuditoriaDeAsync(f.Documento.Codigo), a => a.Accion == "Documento.CambiarResponsable");
    }
}
