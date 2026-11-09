using System.Text;
using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Consulta;

public class TextoPdfTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static MemoryStream Bytes(byte[] b) => new(b);

    private static async Task<string?> TextoGuardadoAsync(Escenario e, int revisionId)
    {
        await using var db = e.Db();
        return (await db.Revisiones.AsNoTracking().SingleAsync(r => r.Id == revisionId)).TextoExtraido;
    }

    // ---------- Al subir ----------

    [Fact]
    public async Task Subir_un_PDF_con_texto_lo_guarda_y_dice_que_sera_buscable()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        var r = await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "x.pdf",
            Bytes(GeneradorPdf.Crear("Procedimiento de calibración de balanzas")));

        Assert.True(r.TieneTextoBuscable);
        Assert.Null(r.Aviso);
        Assert.Contains("calibración", await TextoGuardadoAsync(e, f.RevisionId));
        Assert.Contains("TextoBuscable=sí", (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.SubirPdf").ValorNuevo);
    }

    [Fact]
    public async Task Un_PDF_escaneado_se_acepta_pero_avisa_que_no_se_buscara_por_contenido()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        var r = await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "escaneo.pdf", Bytes(GeneradorPdf.Crear("")));

        Assert.False(r.TieneTextoBuscable);
        Assert.Contains("escaneado", r.Aviso);
        Assert.Equal("", await TextoGuardadoAsync(e, f.RevisionId));               // "" = ya leído, sin texto
        Assert.True((await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).TienePdf); // el archivo sí quedó guardado
        await e.IniciarAsync(f);                                                     // y se puede enviar a revisión igual
        Assert.Equal(EstadoRevision.EnRevision, await e.EstadoRevisionAsync(f.RevisionId));
        Assert.Contains("TextoBuscable=no", (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Revision.SubirPdf").ValorNuevo);
    }

    [Fact]
    public async Task Un_PDF_danado_con_firma_valida_se_acepta_con_un_aviso_distinto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var danado = Encoding.ASCII.GetBytes("%PDF-1.4\nesto empieza como un PDF pero no lo es");

        var r = await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "roto.pdf", Bytes(danado));

        Assert.False(r.TieneTextoBuscable);
        Assert.Contains("No se pudo leer", r.Aviso);
        Assert.True((await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).TienePdf);
    }

    [Fact]
    public async Task Subir_la_fuente_editable_no_toca_el_texto_ni_avisa()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        await e.SubirPdfAsync(f.Autor, f.RevisionId, "contenido original");
        var antes = await TextoGuardadoAsync(e, f.RevisionId);

        var r = await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "origen.docx", Bytes([0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4]));

        Assert.False(r.TieneTextoBuscable);
        Assert.Null(r.Aviso);
        Assert.Equal(antes, await TextoGuardadoAsync(e, f.RevisionId));
    }

    [Fact]
    public async Task Reemplazar_el_PDF_reemplaza_tambien_el_texto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        await e.SubirPdfAsync(f.Autor, f.RevisionId, "primera version alfa");

        await e.SubirPdfAsync(f.Autor, f.RevisionId, "segunda version omega");

        var texto = await TextoGuardadoAsync(e, f.RevisionId);
        Assert.Contains("omega", texto);
        Assert.DoesNotContain("alfa", texto);
    }

    [Fact]
    public async Task Reemplazar_un_PDF_con_texto_por_uno_escaneado_borra_el_texto_viejo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        await e.SubirPdfAsync(f.Autor, f.RevisionId, "contenido que ya no aplica");

        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "escaneo.pdf", Bytes(GeneradorPdf.Crear("")));

        Assert.Equal("", await TextoGuardadoAsync(e, f.RevisionId)); // no queda texto de un archivo que ya no existe
    }

    // ---------- Indexado de lo ya publicado ----------

    private static async Task BorrarTextoAsync(Escenario e, int revisionId)
    {
        await using var db = e.Db();
        await db.Revisiones.Where(r => r.Id == revisionId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TextoExtraido, (string?)null));
    }

    [Fact]
    public async Task El_indexador_lee_el_texto_de_lo_publicado_antes_de_existir_la_busqueda()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var palabra = Escenario.PalabraUnica();
        await e.SubirPdfAsync(f.Autor, f.RevisionId, $"contenido {palabra}");
        await e.PublicarPrimeraAsync(f);
        await BorrarTextoAsync(e, f.RevisionId); // como un documento publicado en la Fase 2
        var lector = await e.NuevoActorAsync([]);
        Assert.Empty((await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(palabra, AreaId: f.AreaId))).Elementos);

        var n = await e.Scope().GetRequiredService<IIndexadorService>().IndexarPendientesAsync();

        Assert.True(n >= 1);
        Assert.Contains(palabra, await TextoGuardadoAsync(e, f.RevisionId));
        Assert.Single((await e.Consulta().BuscarAsync(lector, new ConsultaBusqueda(palabra, AreaId: f.AreaId))).Elementos);
    }

    [Fact]
    public async Task El_indexador_es_idempotente_y_no_reintenta_lo_ya_leido()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "escaneo.pdf", Bytes(GeneradorPdf.Crear("")));
        await e.PublicarPrimeraAsync(f);
        await BorrarTextoAsync(e, f.RevisionId);
        var indexador = e.Scope().GetRequiredService<IIndexadorService>();

        var primera = await indexador.IndexarPendientesAsync();
        var segunda = await indexador.IndexarPendientesAsync();

        Assert.True(primera >= 1);
        Assert.Equal("", await TextoGuardadoAsync(e, f.RevisionId)); // sin texto, pero marcado como ya intentado
        Assert.Equal(0, segunda);                                    // un escaneo no se vuelve a leer en cada pasada
    }

    [Fact]
    public async Task El_indexador_ignora_los_borradores()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(); // PDF subido, sigue en borrador
        await BorrarTextoAsync(e, f.RevisionId);

        await e.Scope().GetRequiredService<IIndexadorService>().IndexarPendientesAsync();

        Assert.Null(await TextoGuardadoAsync(e, f.RevisionId));
    }

    [Fact]
    public async Task Si_el_archivo_falta_el_indexador_no_falla_y_lo_deja_pendiente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        await BorrarTextoAsync(e, f.RevisionId);
        Directory.Delete(e.RutaArchivos, recursive: true); // incluso falta la carpeta entera del almacén

        var n = await e.Scope().GetRequiredService<IIndexadorService>().IndexarPendientesAsync();

        Assert.Equal(0, n);
        Assert.Null(await TextoGuardadoAsync(e, f.RevisionId)); // sigue pendiente: se retomará cuando el archivo vuelva
    }

    [Fact]
    public async Task El_indexador_respeta_el_maximo_por_pasada()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var ids = new List<int> { f.RevisionId };
        await e.PublicarPrimeraAsync(f);
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await e.PublicarOtroAsync(f, $"Otro {i}", $"texto {i}")).RevisionId);
        }

        foreach (var id in ids)
        {
            await BorrarTextoAsync(e, id);
        }

        var n = await e.Scope().GetRequiredService<IIndexadorService>().IndexarPendientesAsync(maximo: 2);

        Assert.Equal(2, n);
        var pendientes = 0;
        foreach (var id in ids)
        {
            pendientes += await TextoGuardadoAsync(e, id) is null ? 1 : 0;
        }

        Assert.Equal(2, pendientes);
    }
}
