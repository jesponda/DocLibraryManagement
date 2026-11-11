using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Reportes;

/// <summary>Consulta de la bitácora: acceso, filtros, orden, paginación y exportación.</summary>
public class AuditoriaServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static FiltroAuditoria Documento(string codigo) => new(EntidadId: codigo);

    // ---------- Acceso ----------

    [Fact]
    public async Task Solo_el_administrador_y_el_auditor_consultan_la_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var permitido in new[] { f.Admin, auditor })
        {
            Assert.NotEmpty((await e.Auditoria.ConsultarAsync(permitido, Documento(f.Documento.Codigo))).Elementos);
            Assert.NotEmpty(await e.Auditoria.ExportarAsync(permitido, Documento(f.Documento.Codigo)));
            Assert.NotEmpty(await e.Auditoria.EntidadesAsync(permitido));
            Assert.NotEmpty(await e.Auditoria.AccionesAsync(permitido));
        }

        foreach (var negado in new[] { f.Autor, f.Revisor, f.Aprobador, await e.NuevoActorAsync([]) })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Auditoria.ConsultarAsync(negado, new FiltroAuditoria()));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Auditoria.ExportarAsync(negado, new FiltroAuditoria()));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Auditoria.EntidadesAsync(negado));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Auditoria.AccionesAsync(negado));
        }
    }

    [Fact]
    public async Task Consultar_la_bitacora_no_escribe_en_ella()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await using var db = e.Db();
        var antes = await db.Auditoria.CountAsync();

        await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria());
        await e.Auditoria.ExportarAsync(f.Admin, new FiltroAuditoria());

        Assert.Equal(antes, await db.Auditoria.CountAsync());
    }

    // ---------- Contenido y orden ----------

    [Fact]
    public async Task Los_eventos_de_un_documento_salen_con_todos_sus_datos_y_de_lo_mas_reciente_a_lo_mas_antiguo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        var eventos = (await e.Auditoria.ConsultarAsync(f.Admin, Documento(f.Documento.Codigo))).Elementos;

        Assert.Equal(eventos.OrderByDescending(x => x.Id).Select(x => x.Id), eventos.Select(x => x.Id));
        var iniciar = Assert.Single(eventos, x => x.Accion == "Flujo.Iniciar");
        Assert.Equal(f.Autor.Identificador, iniciar.Usuario);
        Assert.Equal("Documento", iniciar.Entidad);
        Assert.Equal(f.Documento.Codigo, iniciar.EntidadId);
        Assert.Contains(eventos, x => x.Accion == "Documento.Crear");
        Assert.True(iniciar.Id > eventos.Single(x => x.Accion == "Documento.Crear").Id);
    }

    [Fact]
    public async Task La_fecha_del_evento_es_la_del_reloj_del_sistema()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var t0 = e.Reloj.GetUtcNow().UtcDateTime;

        var crear = (await e.Auditoria.ConsultarAsync(f.Admin, Documento(f.Documento.Codigo))).Elementos.Single(x => x.Accion == "Documento.Crear");

        Assert.Equal(t0, crear.FechaUtc);
    }

    // ---------- Filtros ----------

    [Fact]
    public async Task El_filtro_de_accion_es_un_prefijo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        var soloFlujo = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(EntidadId: f.Documento.Codigo, Accion: "Flujo."));
        var exacta = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(EntidadId: f.Documento.Codigo, Accion: "documento.crear"));
        var enMedio = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(EntidadId: f.Documento.Codigo, Accion: "crear")); // no es prefijo

        Assert.All(soloFlujo.Elementos, x => Assert.StartsWith("Flujo.", x.Accion));
        Assert.NotEmpty(soloFlujo.Elementos);
        Assert.Single(exacta.Elementos);                         // sin distinguir mayúsculas
        Assert.Empty(enMedio.Elementos);
    }

    [Fact]
    public async Task El_filtro_de_usuario_busca_dentro_del_identificador()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var fragmento = f.Autor.Identificador[5..];              // sin «TEST\»

        var r = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(Usuario: fragmento));

        Assert.NotEmpty(r.Elementos);
        Assert.All(r.Elementos, x => Assert.Contains(fragmento, x.Usuario, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task El_filtro_de_entidad_es_exacto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var documentos = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(Entidad: "Documento", EntidadId: f.Documento.Codigo));
        var usuarios = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(Entidad: "Usuario", EntidadId: f.Documento.Codigo));

        Assert.NotEmpty(documentos.Elementos);
        Assert.Empty(usuarios.Elementos);
    }

    [Fact]
    public async Task El_texto_busca_en_comentario_y_valores_sin_distinguir_acentos_ni_mayusculas()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Se retira por obsolescencia técnica");

        var porComentario = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(EntidadId: f.Documento.Codigo, Texto: "OBSOLESCENCIA TECNICA"));
        var porValorNuevo = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(EntidadId: f.Documento.Codigo, Texto: "retirado"));

        Assert.Equal("Documento.Retirar", Assert.Single(porComentario.Elementos).Accion);
        Assert.Contains(porValorNuevo.Elementos, x => x.Accion == "Documento.Retirar");
    }

    [Fact]
    public async Task Los_comodines_se_buscan_literal()
    {
        using var e = new Escenario(bd);
        var marca = Escenario.PalabraUnica();
        var a = await e.PrepararFlujoAsync();
        var b = await e.PrepararFlujoAsync();
        await e.Revisiones.RetirarDocumentoAsync(a.Admin, a.Documento.Id, $"{marca}100% completo");
        await e.Revisiones.RetirarDocumentoAsync(b.Admin, b.Documento.Id, $"{marca}100X completo");

        var r = await e.Auditoria.ConsultarAsync(a.Admin, new FiltroAuditoria(Texto: $"{marca}100%"));

        Assert.Equal(a.Documento.Codigo, Assert.Single(r.Elementos).EntidadId); // «%» no es comodín: «100X» no coincide
    }

    [Fact]
    public async Task El_filtro_de_fechas_incluye_el_inicio_y_excluye_el_final()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var t0 = e.Reloj.GetUtcNow().UtcDateTime;
        e.Reloj.Advance(TimeSpan.FromDays(2));
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "Ya no aplica");
        var t1 = e.Reloj.GetUtcNow().UtcDateTime;

        var antes = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(t0.AddDays(-1), t0.AddDays(1), EntidadId: f.Documento.Codigo));
        var despues = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(t0.AddDays(1), t1.AddDays(1), EntidadId: f.Documento.Codigo));
        var exactoInicio = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(t1, null, EntidadId: f.Documento.Codigo, Accion: "Documento.Retirar"));
        var exactoFinal = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(null, t1, EntidadId: f.Documento.Codigo, Accion: "Documento.Retirar"));

        Assert.DoesNotContain(antes.Elementos, x => x.Accion == "Documento.Retirar");
        Assert.Contains(antes.Elementos, x => x.Accion == "Documento.Crear");
        Assert.Equal("Documento.Retirar", Assert.Single(despues.Elementos).Accion);
        Assert.Single(exactoInicio.Elementos);                  // «desde» incluye ese instante
        Assert.Empty(exactoFinal.Elementos);                    // «hasta» lo excluye
    }

    [Fact]
    public async Task Los_filtros_se_combinan_con_y()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        var r = await e.Auditoria.ConsultarAsync(f.Admin, new FiltroAuditoria(
            Usuario: f.Autor.Identificador[5..], Accion: "Flujo.", Entidad: "Documento", EntidadId: f.Documento.Codigo));

        Assert.Equal("Flujo.Iniciar", Assert.Single(r.Elementos).Accion);
    }

    [Fact]
    public async Task Un_filtro_desmesurado_se_rechaza()
    {
        using var e = new Escenario(bd);
        var admin = await e.NuevoAdminAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Auditoria.ConsultarAsync(admin, new FiltroAuditoria(Texto: new string('x', 201))));
        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Auditoria.ExportarAsync(admin, new FiltroAuditoria(Usuario: new string('x', 201))));
    }

    // ---------- Paginación y exportación ----------

    [Fact]
    public async Task La_paginacion_reparte_los_eventos_sin_repetir_ni_perder()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.Flujo.DecidirAsync(f.Revisor, (await e.UltimoFlujoAsync(f)).Pasos[0].Id, true, null);
        var filtro = Documento(f.Documento.Codigo);

        var todo = await e.Auditoria.ConsultarAsync(f.Admin, filtro, pagina: 1, tamano: 100);
        var p1 = await e.Auditoria.ConsultarAsync(f.Admin, filtro, pagina: 1, tamano: 2);
        var p2 = await e.Auditoria.ConsultarAsync(f.Admin, filtro, pagina: 2, tamano: 2);
        var p3 = await e.Auditoria.ConsultarAsync(f.Admin, filtro, pagina: 3, tamano: 2);

        Assert.True(todo.Total >= 3);
        Assert.Equal(todo.Total, p1.Total);
        Assert.Equal((int)Math.Ceiling(todo.Total / 2.0), p1.TotalPaginas);
        Assert.Equal(todo.Elementos.Select(x => x.Id), p1.Elementos.Concat(p2.Elementos).Concat(p3.Elementos).Select(x => x.Id));
    }

    [Fact]
    public async Task La_exportacion_trae_lo_mismo_que_la_consulta()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var filtro = Documento(f.Documento.Codigo);

        var consulta = await e.Auditoria.ConsultarAsync(f.Admin, filtro, tamano: 100);
        var exportada = await e.Auditoria.ExportarAsync(f.Admin, filtro);

        Assert.Equal(consulta.Elementos.Select(x => x.Id), exportada.Select(x => x.Id));
    }

    [Fact]
    public async Task La_exportacion_a_excel_conserva_el_texto_de_la_bitacora_sin_interpretarlo_como_formula()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.RetirarDocumentoAsync(f.Admin, f.Documento.Id, "=HYPERLINK(\"http://malo\")");
        var eventos = await e.Auditoria.ExportarAsync(f.Admin, Documento(f.Documento.Codigo));

        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(e.Exportador.AXlsx(TablasReporte.Auditoria(eventos))));

        var hoja = libro.Worksheet(1);
        var celdas = hoja.CellsUsed().Where(c => c.GetString().Contains("HYPERLINK")).ToList();
        Assert.NotEmpty(celdas);
        Assert.All(celdas, c => Assert.False(c.HasFormula));
    }

    // ---------- Listas para el filtro ----------

    [Fact]
    public async Task Las_listas_de_entidades_y_acciones_traen_lo_que_hay_sin_repetir_y_ordenado()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        var entidades = await e.Auditoria.EntidadesAsync(f.Admin);
        var acciones = await e.Auditoria.AccionesAsync(f.Admin);

        Assert.Contains("Documento", entidades);
        Assert.Contains("Documento.Crear", acciones);
        Assert.Equal(entidades.Distinct().OrderBy(x => x), entidades);
        Assert.Equal(acciones.Distinct().OrderBy(x => x), acciones);
    }
}
