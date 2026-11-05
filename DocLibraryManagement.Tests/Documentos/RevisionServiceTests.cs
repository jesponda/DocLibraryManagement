using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Documentos;

public class RevisionServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static MemoryStream Bytes(byte[] datos) => new(datos);

    private static byte[] Docx() => [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8];

    private static async Task<byte[]> Leer(Stream s)
    {
        await using (s)
        {
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    // ---------- Edición del borrador ----------

    [Fact]
    public async Task El_autor_actualiza_motivo_y_descripcion_y_queda_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, "  Alta del procedimiento  ", "Cubre el arranque de línea");

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.Equal("Alta del procedimiento", r.MotivoCambio);
        Assert.Equal("Cubre el arranque de línea", r.DescripcionCambio);
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Revision.ActualizarBorrador");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task El_motivo_es_obligatorio(string motivo)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, motivo, null));
    }

    [Fact]
    public async Task Motivo_y_descripcion_tienen_limite()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, new string('x', 1001), null));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, "ok", new string('x', 4001)));
    }

    [Fact]
    public async Task Un_autor_de_otra_area_y_un_lector_no_modifican_el_borrador()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var ajeno = await e.NuevoAutorAsync("OTRA");
        var lector = await e.NuevoActorAsync([]);

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ActualizarBorradorAsync(ajeno, f.RevisionId, "x", null));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ActualizarBorradorAsync(lector, f.RevisionId, "x", null));
    }

    [Fact]
    public async Task Una_revision_enviada_a_flujo_ya_no_se_edita()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, "tarde", null));
        Assert.Contains("borrador", ex.Message);
    }

    // ---------- Subida de archivos ----------

    [Fact]
    public async Task Subir_un_PDF_lo_guarda_con_hash_y_nombre_generado_dentro_del_almacen()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var contenido = Escenario.PdfDePrueba("uno");

        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "../../Mi Procedimiento (final).pdf", Bytes(contenido));

        await using var db = e.Db();
        var rev = await db.Revisiones.SingleAsync(r => r.Id == f.RevisionId);
        Assert.NotNull(rev.RutaArchivoPdf);
        Assert.DoesNotContain("Procedimiento", rev.RutaArchivoPdf);   // nunca el nombre original
        Assert.DoesNotContain("..", rev.RutaArchivoPdf);
        Assert.EndsWith(".pdf", rev.RutaArchivoPdf);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(contenido).Select(b => b.ToString("x2")).Aggregate((a, b) => a + b), rev.HashArchivoPdf);

        var enDisco = Path.Combine(e.RutaArchivos, rev.RutaArchivoPdf!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(enDisco));
        Assert.Equal(contenido, await File.ReadAllBytesAsync(enDisco));
        Assert.Empty(Directory.GetFiles(e.RutaArchivos, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Reemplazar_el_PDF_borra_el_anterior_y_actualiza_el_hash()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        await e.SubirPdfAsync(f.Autor, f.RevisionId, "v1");
        var antes = (await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).HashPdf;
        await using var db = e.Db();
        var rutaVieja = (await db.Revisiones.AsNoTracking().SingleAsync(r => r.Id == f.RevisionId)).RutaArchivoPdf!;

        await e.SubirPdfAsync(f.Autor, f.RevisionId, "v2");

        var despues = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.NotEqual(antes, despues.HashPdf);
        Assert.False(File.Exists(Path.Combine(e.RutaArchivos, rutaVieja.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Single(Directory.GetFiles(e.RutaArchivos, "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task La_fuente_editable_es_opcional_y_se_guarda_aparte()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "origen.docx", Bytes(Docx()));

        var r = await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId);
        Assert.True(r.TienePdf);
        Assert.True(r.TieneFuente);
        Assert.NotEqual(r.HashPdf, r.HashFuente);
    }

    [Fact]
    public async Task Un_ejecutable_con_extension_pdf_se_rechaza_y_no_deja_rastro()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var exe = System.Text.Encoding.ASCII.GetBytes("MZ\x90\x00 esto no es un pdf");

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "factura.pdf", Bytes(exe)));

        Assert.Contains("no corresponde", ex.Message);
        Assert.False((await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId)).TienePdf);
        Assert.Empty(Directory.Exists(e.RutaArchivos) ? Directory.GetFiles(e.RutaArchivos, "*", SearchOption.AllDirectories) : []);
    }

    [Theory]
    [InlineData("documento.docx")]
    [InlineData("documento.exe")]
    [InlineData("documento")]
    public async Task El_PDF_publicado_solo_acepta_extension_pdf(string nombre)
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, nombre, Bytes(Escenario.PdfDePrueba())));
    }

    [Fact]
    public async Task Un_archivo_vacio_se_rechaza()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "vacio.pdf", Bytes([])));
    }

    [Fact]
    public async Task Un_archivo_mayor_al_limite_se_rechaza_y_se_limpia_el_temporal()
    {
        using var e = new Escenario(bd, tamanoMaximoMb: 1);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var grande = Escenario.PdfDePrueba().Concat(new byte[1024 * 1024 + 10]).ToArray();

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() =>
            e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "grande.pdf", Bytes(grande)));

        Assert.Contains("1 MB", ex.Message);
        Assert.Empty(Directory.Exists(e.RutaArchivos) ? Directory.GetFiles(e.RutaArchivos, "*", SearchOption.AllDirectories) : []);
    }

    [Fact]
    public async Task No_se_sube_un_archivo_si_la_revision_ya_esta_en_flujo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.SubirPdfAsync(f.Autor, f.RevisionId, "cambiado"));
        Assert.Single(Directory.GetFiles(e.RutaArchivos, "*.pdf", SearchOption.AllDirectories)); // solo el original
    }

    [Fact]
    public async Task Sin_permiso_de_area_no_se_sube_nada()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var ajeno = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.SubirPdfAsync(ajeno, f.RevisionId));
    }

    // ---------- Descarga ----------

    [Fact]
    public async Task El_autor_descarga_su_PDF_con_el_nombre_del_documento_y_su_contenido_intacto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);
        var contenido = Escenario.PdfDePrueba("descarga");
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf, "x.pdf", Bytes(contenido));

        var d = await e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf);

        Assert.Equal($"{f.Documento.Codigo}_Rev.00.pdf", d.NombreDescarga);
        Assert.Equal("application/pdf", d.TipoContenido);
        Assert.True(d.EnLinea);
        Assert.Equal(contenido, await Leer(d.Contenido));
    }

    [Fact]
    public async Task La_fuente_se_descarga_como_adjunto()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "x.docx", Bytes(Docx()));

        var d = await e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente);

        Assert.False(d.EnLinea);
        Assert.EndsWith(".docx", d.NombreDescarga);
        await d.Contenido.DisposeAsync();
    }

    [Fact]
    public async Task Quien_tiene_una_etapa_asignada_ve_el_PDF_pero_no_la_fuente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "x.docx", Bytes(Docx()));
        await e.IniciarAsync(f);

        await (await e.Revisiones.AbrirArchivoAsync(f.Revisor, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync();
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(f.Revisor, f.RevisionId, TipoArchivo.Fuente));
    }

    [Fact]
    public async Task Un_lector_sin_etapa_no_descarga_un_borrador_ni_la_fuente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "x.docx", Bytes(Docx()));
        var lector = await e.NuevoActorAsync([]);
        var otroRevisor = await e.NuevoRevisorAsync(); // tiene el rol, pero no una etapa en ESTE documento

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(otroRevisor, f.RevisionId, TipoArchivo.Pdf));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Fuente));
    }

    [Fact]
    public async Task Auditor_y_Administrador_descargan_pdf_y_fuente()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.SubirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Fuente, "x.docx", Bytes(Docx()));
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var quien in new[] { auditor, f.Admin })
        {
            await (await e.Revisiones.AbrirArchivoAsync(quien, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync();
            await (await e.Revisiones.AbrirArchivoAsync(quien, f.RevisionId, TipoArchivo.Fuente)).Contenido.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pedir_un_archivo_que_no_existe_da_un_mensaje_claro()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync(conPdf: false);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf));
        Assert.Contains("todavía no tiene PDF", ex.Message);
    }

    [Fact]
    public async Task Un_archivo_alterado_en_disco_se_detecta_no_se_entrega_y_queda_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await using (var db = e.Db())
        {
            var ruta = (await db.Revisiones.SingleAsync(r => r.Id == f.RevisionId)).RutaArchivoPdf!;
            var enDisco = Path.Combine(e.RutaArchivos, ruta.Replace('/', Path.DirectorySeparatorChar));
            await File.AppendAllTextAsync(enDisco, "\n% alterado por alguien");
        }

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf));

        Assert.Contains("integridad", ex.Message);
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Archivo.IntegridadFallida");
    }

    [Fact]
    public async Task Un_archivo_borrado_del_almacen_se_reporta_y_queda_en_bitacora()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        foreach (var archivo in Directory.GetFiles(e.RutaArchivos, "*.pdf", SearchOption.AllDirectories))
        {
            File.Delete(archivo);
        }

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.AbrirArchivoAsync(f.Autor, f.RevisionId, TipoArchivo.Pdf));
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Archivo.IntegridadFallida");
    }

    [Fact]
    public async Task Una_revision_vigente_es_de_lectura_abierta_y_una_obsoleta_deja_rastro_al_descargarse()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var lector = await e.NuevoActorAsync([]);
        await using (var db = e.Db())
        {
            await db.Revisiones.Where(r => r.Id == f.RevisionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Estado, EstadoRevision.Vigente));
        }

        await (await e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync();
        Assert.Equal(f.Documento.Codigo, (await e.Revisiones.ObtenerAsync(lector, f.RevisionId)).CodigoDocumento);

        await using (var db = e.Db())
        {
            await db.Revisiones.Where(r => r.Id == f.RevisionId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Estado, EstadoRevision.Obsoleto));
        }

        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.AbrirArchivoAsync(lector, f.RevisionId, TipoArchivo.Pdf));
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);
        await (await e.Revisiones.AbrirArchivoAsync(auditor, f.RevisionId, TipoArchivo.Pdf)).Contenido.DisposeAsync();
        Assert.Contains(await e.AuditoriaDeAsync(f.Documento.Codigo), x => x.Accion == "Revision.DescargarObsoleta");
    }

    // ---------- Consulta ----------

    [Fact]
    public async Task Obtener_exige_permiso_de_lectura_sobre_un_borrador()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var lector = await e.NuevoActorAsync([]);

        Assert.NotNull(await e.Revisiones.ObtenerAsync(f.Autor, f.RevisionId));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.ObtenerAsync(lector, f.RevisionId));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ObtenerAsync(f.Admin, int.MaxValue));
    }

    // ---------- Cancelar documento nuevo ----------

    [Fact]
    public async Task Cancelar_un_documento_nuevo_lo_retira_y_cancela_su_revision_sin_liberar_el_codigo()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();

        await e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "Se creó por error");

        var doc = await e.Documentos.ObtenerAsync(f.Autor, f.Documento.Id);
        Assert.Equal(EstadoDocumento.Retirado, doc!.Estado);
        Assert.Equal(EstadoRevision.Cancelado, doc.Revisiones.Single().Estado);
        var evento = (await e.AuditoriaDeAsync(f.Documento.Codigo)).Single(x => x.Accion == "Documento.CancelarNuevo");
        Assert.Equal("Se creó por error", evento.Comentario);

        // El siguiente documento del mismo tipo y área NO reutiliza el consecutivo 001.
        var otro = await e.Documentos.CrearAsync(f.Autor, new(("Otro"), f.AreaId, null, f.TipoId));
        Assert.EndsWith("-002", otro.Codigo);
    }

    [Fact]
    public async Task Cancelar_exige_motivo_permiso_y_que_siga_siendo_un_borrador_inicial()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        var ajeno = await e.NuevoAutorAsync("OTRA");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "  "));
        await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Revisiones.CancelarDocumentoNuevoAsync(ajeno, f.Documento.Id, "no es mío"));

        await e.IniciarAsync(f);
        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "ya en flujo"));
        Assert.Contains("flujo", ex.Message);
    }

    [Fact]
    public async Task Un_documento_cancelado_no_admite_mas_cambios_ni_se_cancela_dos_veces()
    {
        using var e = new Escenario(bd);
        var f = await e.PrepararFlujoAsync();
        await e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "error");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.CancelarDocumentoNuevoAsync(f.Autor, f.Documento.Id, "otra vez"));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Revisiones.ActualizarBorradorAsync(f.Autor, f.RevisionId, "x", null));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.SubirPdfAsync(f.Autor, f.RevisionId, "nuevo"));
    }
}
