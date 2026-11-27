using System.Text;
using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Tests.Archivos;

public class ReglasArchivoTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n....");
    private static readonly byte[] Zip = [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0];
    private static readonly byte[] Ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0];
    private static readonly byte[] Exe = Encoding.ASCII.GetBytes("MZ\x90\x00 programa");

    [Theory]
    [InlineData("procedimiento.pdf", ".pdf")]
    [InlineData("PROCEDIMIENTO.PDF", ".pdf")]
    [InlineData("a.b.c.pdf", ".pdf")]
    public void El_PDF_acepta_solo_la_extension_pdf_sin_importar_mayusculas(string nombre, string esperada)
    {
        Assert.Equal(esperada, ReglasArchivo.ValidarExtension(TipoArchivo.Pdf, nombre));
    }

    [Theory]
    [InlineData("doc.docx")]
    [InlineData("doc.pdf.exe")]
    [InlineData("doc")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("doc.pdf ")]
    public void El_PDF_rechaza_otras_extensiones(string? nombre)
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarExtension(TipoArchivo.Pdf, nombre));
    }

    [Theory]
    [InlineData("a.doc")]
    [InlineData("a.DOCX")]
    [InlineData("a.xls")]
    [InlineData("a.xlsx")]
    [InlineData("a.xlsm")]
    [InlineData("a.odt")]
    [InlineData("a.ods")]
    [InlineData("a.ppt")]
    [InlineData("a.PPTX")]
    [InlineData("a.pptm")]
    [InlineData("a.odp")]
    public void La_fuente_acepta_formatos_de_oficina(string nombre)
    {
        Assert.NotNull(ReglasArchivo.ValidarExtension(TipoArchivo.Fuente, nombre));
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.exe")]
    [InlineData("a.zip")]
    [InlineData("a.ps1")]
    [InlineData("a.html")]
    [InlineData("a.js")]
    [InlineData("a.pps")]
    [InlineData("a.ppsm")]
    public void La_fuente_rechaza_ejecutables_scripts_y_otros(string nombre)
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarExtension(TipoArchivo.Fuente, nombre));
    }

    [Fact]
    public void Una_firma_correcta_pasa_para_cada_formato()
    {
        ReglasArchivo.ValidarCabecera(".pdf", Pdf);
        ReglasArchivo.ValidarCabecera(".docx", Zip);
        ReglasArchivo.ValidarCabecera(".xlsx", Zip);
        ReglasArchivo.ValidarCabecera(".xlsm", Zip);
        ReglasArchivo.ValidarCabecera(".odt", Zip);
        ReglasArchivo.ValidarCabecera(".pptx", Zip);
        ReglasArchivo.ValidarCabecera(".pptm", Zip);
        ReglasArchivo.ValidarCabecera(".odp", Zip);
        ReglasArchivo.ValidarCabecera(".ppt", Ole);
        ReglasArchivo.ValidarCabecera(".doc", Ole);
        ReglasArchivo.ValidarCabecera(".xls", Ole);
    }

    [Fact]
    public void Un_ejecutable_renombrado_como_pdf_se_rechaza_por_su_firma()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".pdf", Exe));
    }

    [Theory]
    [InlineData(".pdf")]
    [InlineData(".docx")]
    [InlineData(".doc")]
    public void Un_archivo_vacio_o_muy_corto_se_rechaza(string extension)
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(extension, []));
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(extension, [0x25]));
    }

    [Fact]
    public void La_firma_debe_coincidir_con_el_formato_declarado()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".docx", Pdf)); // un PDF disfrazado de Word
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".doc", Zip)); // docx con extensión .doc
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".pptx", Pdf)); // un PDF disfrazado de PowerPoint
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".ppt", Zip)); // pptx con extensión .ppt
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".pdf", Zip));
    }

    [Fact]
    public void Una_extension_desconocida_nunca_pasa_la_cabecera()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasArchivo.ValidarCabecera(".exe", Exe));
    }

    [Theory]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData(".ppt", "application/vnd.ms-powerpoint")]
    [InlineData(".odp", "application/vnd.oasis.opendocument.presentation")]
    [InlineData(".desconocida", "application/octet-stream")]
    public void El_tipo_de_contenido_se_deduce_de_la_extension(string extension, string esperado)
    {
        Assert.Equal(esperado, ReglasArchivo.TipoContenido(extension));
    }
}
