using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Documentos;

public class DocumentoServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static NuevoDocumento Nuevo(int areaId, int tipoId, string titulo = "Procedimiento de calidad", int? subAreaId = null) =>
        new(titulo, areaId, subAreaId, tipoId);

    [Fact]
    public async Task Crear_asigna_el_codigo_TTT_AAA_NNN_y_la_revision_Rev00_en_borrador()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, codigoTipo) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoAutorAsync(codigoArea);

        var doc = await e.Documentos.CrearAsync(autor, Nuevo(areaId, tipoId));

        Assert.Equal($"{codigoTipo}-{codigoArea}-001", doc.Codigo);
        Assert.Equal(EstadoDocumento.Activo, doc.Estado);
        var rev = Assert.Single(doc.Revisiones);
        Assert.Equal("Rev.00", rev.Etiqueta);
        Assert.Equal(EstadoRevision.Borrador, rev.Estado);
        Assert.Equal("Emisión inicial", rev.MotivoCambio);
    }

    [Fact]
    public async Task El_consecutivo_avanza_por_tipo_y_area_de_forma_independiente()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (area1, cod1, tipo1, _) = await e.NuevaAreaConTipoAsync(admin);
        var (area2, cod2, tipo2, _) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoActorAsync([RolSistema.Autor], cod1, cod2);

        var a1 = await e.Documentos.CrearAsync(autor, Nuevo(area1, tipo1));
        var a2 = await e.Documentos.CrearAsync(autor, Nuevo(area1, tipo1));
        var b1 = await e.Documentos.CrearAsync(autor, Nuevo(area2, tipo2));

        Assert.EndsWith("-001", a1.Codigo);
        Assert.EndsWith("-002", a2.Codigo);
        Assert.EndsWith("-001", b1.Codigo);
    }

    [Fact]
    public async Task Crear_deja_un_evento_de_bitacora_con_el_usuario_y_el_codigo()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoAutorAsync(codigoArea);

        var doc = await e.Documentos.CrearAsync(autor, Nuevo(areaId, tipoId));

        var evento = Assert.Single(await e.AuditoriaDeAsync(doc.Codigo));
        Assert.Equal("Documento.Crear", evento.Accion);
        Assert.Equal(autor.Identificador, evento.Usuario);
        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime, evento.FechaUtc);
    }

    [Fact]
    public async Task El_responsable_y_el_autor_de_la_revision_son_quien_crea()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoAutorAsync(codigoArea);

        var doc = await e.Documentos.CrearAsync(autor, Nuevo(areaId, tipoId));

        await using var db = e.Db();
        var usuario = await db.Usuarios.SingleAsync(u => u.Identificador == autor.Identificador);
        var guardado = await db.Documentos.Include(d => d.Revisiones).SingleAsync(d => d.Id == doc.Id);
        Assert.Equal(usuario.Id, guardado.ResponsableId);
        Assert.Equal(usuario.Id, guardado.Revisiones.Single().AutorId);
    }

    [Fact]
    public async Task Un_Autor_no_crea_en_un_area_que_no_es_suya()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var autorDeOtra = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CrearAsync(autorDeOtra, Nuevo(areaId, tipoId)));
    }

    [Theory]
    [InlineData(RolSistema.Lector)]
    [InlineData(RolSistema.Revisor)]
    [InlineData(RolSistema.Aprobador)]
    [InlineData(RolSistema.Auditor)]
    public async Task Solo_Autor_y_Administrador_crean_documentos(RolSistema rol)
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var actor = await e.NuevoActorAsync([rol], codigoArea); // incluso con el área asignada

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CrearAsync(actor, Nuevo(areaId, tipoId)));
    }

    [Fact]
    public async Task El_Administrador_crea_en_cualquier_area()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);

        var doc = await e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId));

        Assert.EndsWith("-001", doc.Codigo);
    }

    [Fact]
    public async Task No_se_crea_en_un_area_desactivada()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        await e.Catalogo.ActualizarAreaAsync(admin, areaId, "Cerrada", activa: false);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId)));
        Assert.Contains("desactivada", ex.Message);
    }

    [Fact]
    public async Task No_se_crea_con_un_tipo_no_asociado_o_desactivado()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var ajeno = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "No asociado", 12);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, ajeno)));

        await e.Catalogo.ActualizarTipoAsync(admin, tipoId, "Apagado", 12, activo: false);
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId)));
    }

    [Fact]
    public async Task La_subarea_debe_ser_del_area_y_aplica_su_propia_lista_de_tipos()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var (otraAreaId, _, _, _) = await e.NuevaAreaConTipoAsync(admin);
        var subDeOtra = await e.Catalogo.CrearSubAreaAsync(admin, otraAreaId, "Ajena");
        var sub = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "Propia");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, subAreaId: subDeOtra)));

        var ok = await e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, subAreaId: sub)); // hereda el tipo del área
        Assert.Equal("Propia", ok.SubArea);

        var otroTipo = await e.Catalogo.CrearTipoAsync(admin, Escenario.CodigoUnico("T"), "Solo subárea", 12);
        await e.Catalogo.EstablecerTiposDeSubAreaAsync(admin, sub, [otroTipo]);
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, subAreaId: sub)));
        await e.Documentos.CrearAsync(admin, Nuevo(areaId, otroTipo, subAreaId: sub));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task El_titulo_es_obligatorio(string titulo)
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, titulo)));
    }

    [Fact]
    public async Task El_titulo_tiene_limite_y_se_recorta()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, new string('x', 301))));
        var doc = await e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, "  Con espacios  "));
        Assert.Equal("Con espacios", doc.Titulo);
    }

    [Fact]
    public async Task Un_usuario_que_nunca_inicio_sesion_recibe_un_mensaje_claro()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var fantasma = new Actor("TEST\\nunca-entro", new HashSet<RolSistema> { RolSistema.Administrador }, new HashSet<string>());

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CrearAsync(fantasma, Nuevo(areaId, tipoId)));
        Assert.Contains("vuelve a entrar", ex.Message);
    }

    [Fact]
    public async Task Creaciones_simultaneas_producen_consecutivos_distintos_y_sin_huecos()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);

        // Escenarios independientes: cada uno con su propio contenedor, como harían usuarios distintos.
        var tareas = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            using var otro = new Escenario(bd);
            return await otro.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId, $"Documento {i}"));
        })).ToArray();

        var creados = await Task.WhenAll(tareas);

        await using var db = e.Db();
        var consecutivos = await db.Documentos.Where(d => d.AreaId == areaId && d.TipoDocumentoId == tipoId)
            .Select(d => d.Consecutivo).OrderBy(c => c).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 10), consecutivos);
        Assert.Equal(10, creados.Select(c => c.Codigo).Distinct().Count());
    }

    [Fact]
    public async Task Un_Autor_lista_solo_los_documentos_de_sus_areas()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (area1, cod1, tipo1, _) = await e.NuevaAreaConTipoAsync(admin);
        var (area2, _, tipo2, _) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoAutorAsync(cod1);
        var mio = await e.Documentos.CrearAsync(admin, Nuevo(area1, tipo1));
        var ajeno = await e.Documentos.CrearAsync(admin, Nuevo(area2, tipo2));

        var lista = await e.Documentos.ListarGestionablesAsync(autor);

        Assert.Contains(lista, d => d.Id == mio.Id);
        Assert.DoesNotContain(lista, d => d.Id == ajeno.Id);
        Assert.Equal("Rev.00", lista.Single(d => d.Id == mio.Id).UltimaRevision);
    }

    [Fact]
    public async Task Administrador_y_Auditor_ven_todos_y_un_Lector_ninguno()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, _, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var doc = await e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId));
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        var lector = await e.NuevoActorAsync([]);

        Assert.Contains(await e.Documentos.ListarGestionablesAsync(admin), d => d.Id == doc.Id);
        Assert.Contains(await e.Documentos.ListarGestionablesAsync(auditor), d => d.Id == doc.Id);
        Assert.Empty(await e.Documentos.ListarGestionablesAsync(lector));
    }

    [Fact]
    public async Task Obtener_exige_permiso_sobre_el_area_y_devuelve_null_si_no_existe()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var doc = await e.Documentos.CrearAsync(admin, Nuevo(areaId, tipoId));
        var autorPropio = await e.NuevoAutorAsync(codigoArea);
        var autorAjeno = await e.NuevoAutorAsync("OTRA");

        Assert.NotNull(await e.Documentos.ObtenerAsync(autorPropio, doc.Id));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.ObtenerAsync(autorAjeno, doc.Id));
        Assert.Null(await e.Documentos.ObtenerAsync(admin, int.MaxValue));
    }

    [Fact]
    public async Task Cambiar_la_subarea_se_audita_y_no_toca_el_codigo()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var autor = await e.NuevoAutorAsync(codigoArea);
        var sub = await e.Catalogo.CrearSubAreaAsync(admin, areaId, "Línea 2");
        var doc = await e.Documentos.CrearAsync(autor, Nuevo(areaId, tipoId));

        await e.Documentos.CambiarSubAreaAsync(autor, doc.Id, sub);

        var despues = await e.Documentos.ObtenerAsync(autor, doc.Id);
        Assert.Equal("Línea 2", despues!.SubArea);
        Assert.Equal(doc.Codigo, despues.Codigo);
        var evento = (await e.AuditoriaDeAsync(doc.Codigo)).Single(x => x.Accion == "Documento.CambiarSubArea");
        Assert.Equal("(sin subárea)", evento.ValorAnterior);
        Assert.Equal("Línea 2", evento.ValorNuevo);

        await e.Documentos.CambiarSubAreaAsync(autor, doc.Id, null);
        Assert.Null((await e.Documentos.ObtenerAsync(autor, doc.Id))!.SubArea);
    }

    [Fact]
    public async Task No_se_cambia_a_una_subarea_de_otra_area_ni_sin_permiso()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await e.NuevaAreaConTipoAsync(admin);
        var (otraAreaId, _, _, _) = await e.NuevaAreaConTipoAsync(admin);
        var subAjena = await e.Catalogo.CrearSubAreaAsync(admin, otraAreaId, "Ajena");
        var autor = await e.NuevoAutorAsync(codigoArea);
        var ajeno = await e.NuevoAutorAsync("OTRA");
        var doc = await e.Documentos.CrearAsync(autor, Nuevo(areaId, tipoId));

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Documentos.CambiarSubAreaAsync(autor, doc.Id, subAjena));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Documentos.CambiarSubAreaAsync(ajeno, doc.Id, null));
    }
}
