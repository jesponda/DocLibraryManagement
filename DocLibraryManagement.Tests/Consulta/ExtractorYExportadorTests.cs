using System.Text;
using ClosedXML.Excel;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Tests.Datos;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Consulta;

public class ExtractorYExportadorTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    // ---------- Extractor de texto ----------

    private static async Task<ExtraccionTexto> ExtraerAsync(Escenario e, byte[] pdf)
    {
        using var s = new MemoryStream(pdf);
        return await e.Scope().GetRequiredService<IExtractorTexto>().ExtraerAsync(s);
    }

    [Fact]
    public async Task Lee_el_texto_de_un_PDF_con_acentos()
    {
        using var e = new Escenario(bd);

        var r = await ExtraerAsync(e, GeneradorPdf.Crear("Procedimiento de calibración de balanzas con bloqueo de energía"));

        Assert.True(r.PudoLeerse);
        Assert.True(r.TieneTexto);
        Assert.Contains("calibración", r.Texto);
        Assert.Contains("energía", r.Texto);
        Assert.Contains("balanzas", r.Texto);
    }

    [Fact]
    public async Task Lee_todas_las_paginas()
    {
        using var e = new Escenario(bd);

        var r = await ExtraerAsync(e, GeneradorPdf.Crear("primera pagina alfa", "segunda pagina beta", "tercera pagina gamma"));

        Assert.Contains("alfa", r.Texto);
        Assert.Contains("beta", r.Texto);
        Assert.Contains("gamma", r.Texto);
    }

    [Fact]
    public async Task Un_PDF_sin_texto_se_lee_bien_pero_no_tiene_texto()
    {
        using var e = new Escenario(bd);

        var r = await ExtraerAsync(e, GeneradorPdf.Crear("")); // como un escaneo: páginas sin ningún carácter

        Assert.True(r.PudoLeerse);
        Assert.False(r.TieneTexto);
        Assert.Equal("", r.Texto);
    }

    [Theory]
    [InlineData("%PDF-1.4\nesto no es un pdf real, solo empieza igual")]
    [InlineData("%PDF-")]
    [InlineData("")]
    public async Task Un_archivo_danado_no_lanza_y_se_reporta_como_ilegible(string contenido)
    {
        using var e = new Escenario(bd);

        var r = await ExtraerAsync(e, Encoding.ASCII.GetBytes(contenido));

        Assert.False(r.PudoLeerse);
        Assert.False(r.TieneTexto);
    }

    [Fact]
    public async Task Un_PDF_truncado_a_la_mitad_no_tumba_la_lectura()
    {
        using var e = new Escenario(bd);
        var completo = GeneradorPdf.Crear("contenido importante del documento");

        var r = await ExtraerAsync(e, completo[..(completo.Length / 2)]);

        Assert.True(r.PudoLeerse || !r.TieneTexto); // lo que importa: no lanzó
    }

    [Fact]
    public async Task El_texto_sale_con_los_espacios_normalizados()
    {
        using var e = new Escenario(bd);

        var r = await ExtraerAsync(e, GeneradorPdf.Crear("uno   dos\t\ttres"));

        Assert.DoesNotContain("  ", r.Texto);
        Assert.DoesNotContain('\t', r.Texto);
    }

    // ---------- Exportador ----------

    private static FilaListaMaestra Fila(string codigo = "PRO-CAL-001", string titulo = "Procedimiento", string? sub = null,
        string responsable = "Laura Pérez", DateTime? aprobada = null) =>
        new(1, 1, codigo, titulo, "CAL – Calidad", sub, "Procedimiento", "Rev.02",
            aprobada ?? new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 16, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 3, 16, 0, 0, 0, DateTimeKind.Utc),
            responsable);

    private static string LeerCsv(byte[] bytes)
    {
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM UTF-8
        return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public void El_CSV_lleva_BOM_encabezados_y_fechas_legibles()
    {
        using var e = new Escenario(bd);
        var csv = LeerCsv(e.Scope().GetRequiredService<IExportadorListaMaestra>().ACsv([Fila(sub: "Línea 1")]));

        var lineas = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Código,Título,Área,Subárea,Tipo,Revisión,Fecha de aprobación,Vigente desde,Próxima revisión,Responsable", lineas[0]);
        Assert.Equal("PRO-CAL-001,Procedimiento,CAL – Calidad,Línea 1,Procedimiento,Rev.02,15/03/2026,16/03/2026,16/03/2027,Laura Pérez", lineas[1]);
    }

    [Fact]
    public void El_CSV_encierra_entre_comillas_lo_que_tiene_comas_comillas_o_saltos_de_linea()
    {
        using var e = new Escenario(bd);

        var csv = LeerCsv(e.Scope().GetRequiredService<IExportadorListaMaestra>().ACsv(
            [Fila(titulo: "Uso de \"EPP\", guantes\ny lentes")]));

        Assert.Contains("\"Uso de \"\"EPP\"\", guantes\ny lentes\"", csv);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://malo\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    public void El_CSV_neutraliza_formulas_que_Excel_ejecutaria(string titulo)
    {
        using var e = new Escenario(bd);

        var csv = LeerCsv(e.Scope().GetRequiredService<IExportadorListaMaestra>().ACsv([Fila(titulo: titulo)]));
        var celdaTitulo = csv.Split("\r\n")[1];

        // La celda empieza con apóstrofo, así que Excel la trata como texto en lugar de calcularla.
        Assert.Contains("'" + titulo.Replace("\"", "\"\""), celdaTitulo);
    }

    [Fact]
    public void Un_titulo_normal_no_recibe_apostrofo()
    {
        using var e = new Escenario(bd);

        var csv = LeerCsv(e.Scope().GetRequiredService<IExportadorListaMaestra>().ACsv([Fila(titulo: "Calibración 2026")]));

        Assert.DoesNotContain("'", csv);
    }

    [Fact]
    public void Sin_filas_el_CSV_trae_solo_el_encabezado()
    {
        using var e = new Escenario(bd);

        var csv = LeerCsv(e.Scope().GetRequiredService<IExportadorListaMaestra>().ACsv([]));

        Assert.Single(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void El_Excel_trae_encabezados_filas_y_fechas_como_fechas()
    {
        using var e = new Escenario(bd);
        var bytes = e.Scope().GetRequiredService<IExportadorListaMaestra>().AXlsx([Fila(sub: "Línea 1"), Fila(codigo: "PRO-CAL-002")]);

        using var libro = new XLWorkbook(new MemoryStream(bytes));
        var hoja = libro.Worksheet("Lista maestra");

        Assert.Equal("Código", hoja.Cell(1, 1).GetString());
        Assert.True(hoja.Cell(1, 1).Style.Font.Bold);
        Assert.Equal("PRO-CAL-001", hoja.Cell(2, 1).GetString());
        Assert.Equal("PRO-CAL-002", hoja.Cell(3, 1).GetString());
        Assert.Equal("Línea 1", hoja.Cell(2, 4).GetString());
        Assert.Equal(XLDataType.DateTime, hoja.Cell(2, 7).DataType);
        Assert.Equal(new DateTime(2026, 3, 15), hoja.Cell(2, 7).GetDateTime());
        Assert.Equal(2, hoja.LastRowUsed()!.RowNumber() - 1);
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("=HYPERLINK(\"http://malo\")")]
    [InlineData("@SUM(A1)")]
    [InlineData("00123")]
    public void En_Excel_el_texto_queda_como_texto_literal_y_nunca_como_formula_ni_numero(string titulo)
    {
        using var e = new Escenario(bd);
        var bytes = e.Scope().GetRequiredService<IExportadorListaMaestra>().AXlsx([Fila(titulo: titulo)]);

        using var libro = new XLWorkbook(new MemoryStream(bytes));
        var celda = libro.Worksheet("Lista maestra").Cell(2, 2);

        Assert.False(celda.HasFormula);
        Assert.Equal(XLDataType.Text, celda.DataType);
        Assert.Equal(titulo, celda.GetString()); // sin apóstrofo ni conversión: "00123" no pierde sus ceros
    }

    [Fact]
    public void El_Excel_sin_filas_es_valido_y_trae_solo_encabezados()
    {
        using var e = new Escenario(bd);
        var bytes = e.Scope().GetRequiredService<IExportadorListaMaestra>().AXlsx([]);

        using var libro = new XLWorkbook(new MemoryStream(bytes));

        Assert.Equal(1, libro.Worksheet("Lista maestra").LastRowUsed()!.RowNumber());
    }

    [Fact]
    public void Las_fechas_se_convierten_a_la_hora_local_de_la_planta()
    {
        using var e = new Escenario(bd);
        e.Reloj.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("planta", TimeSpan.FromHours(-6), "planta", "planta"));
        var exportador = e.Scope().GetRequiredService<IExportadorListaMaestra>();
        // 02:00 UTC del día 16 es todavía el día 15 a las 20:00 en UTC-6.
        var fila = Fila(aprobada: new DateTime(2026, 3, 16, 2, 0, 0, DateTimeKind.Utc));

        var csv = LeerCsv(exportador.ACsv([fila]));

        Assert.Contains(",15/03/2026,", csv);
    }
}
