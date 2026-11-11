using System.Text;
using ClosedXML.Excel;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Reportes;

/// <summary>El exportador genérico de los reportes (CSV y Excel).</summary>
public class ExportadorTablasTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static readonly DateTime Utc = new(2026, 11, 10, 14, 30, 0, DateTimeKind.Utc);

    private static string Texto(byte[] csv) => new UTF8Encoding(false).GetString(csv.AsSpan(3)); // sin el BOM

    private static TablaExportable Tabla(params object?[] fila) =>
        new("Hoja", [.. Enumerable.Range(1, fila.Length).Select(i => "C" + i)], [fila]);

    // ---------- CSV ----------

    [Fact]
    public void El_csv_lleva_bom_utf8_para_que_excel_respete_los_acentos()
    {
        using var e = new Escenario(bd);

        var bytes = e.Exportador.ACsv(Tabla("Calibración"));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("Calibración", Texto(bytes));
    }

    [Fact]
    public void El_csv_trae_encabezados_y_una_fila_por_registro_con_saltos_crlf()
    {
        using var e = new Escenario(bd);

        var texto = Texto(e.Exportador.ACsv(new TablaExportable("H", ["A", "B"], [["1", "2"], ["3", "4"]])));

        Assert.Equal("A,B\r\n1,2\r\n3,4\r\n", texto);
    }

    [Fact]
    public void El_csv_escapa_comas_comillas_y_saltos_de_linea()
    {
        using var e = new Escenario(bd);

        var texto = Texto(e.Exportador.ACsv(Tabla("uno, dos", "dijo \"hola\"", "linea1\nlinea2")));

        Assert.Contains("\"uno, dos\"", texto);
        Assert.Contains("\"dijo \"\"hola\"\"\"", texto);
        Assert.Contains("\"linea1\nlinea2\"", texto);
    }

    [Theory]
    [InlineData("=CMD|' /C calc'!A0")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1)")]
    public void El_csv_neutraliza_las_formulas_en_el_texto(string peligroso)
    {
        using var e = new Escenario(bd);

        var texto = Texto(e.Exportador.ACsv(Tabla(peligroso)));

        var filaDeDatos = texto.Split("\r\n")[1];
        Assert.Equal("'" + peligroso, filaDeDatos); // el apóstrofo evita que Excel la ejecute como fórmula
    }

    [Fact]
    public void Un_numero_negativo_no_se_confunde_con_una_formula()
    {
        using var e = new Escenario(bd);

        var texto = Texto(e.Exportador.ACsv(Tabla(-3, 12, 0)));

        Assert.EndsWith("\r\n-3,12,0\r\n", texto); // los días de atraso salen como número, sin apóstrofo
    }

    [Fact]
    public void El_csv_muestra_las_fechas_en_hora_local_las_solo_fecha_sin_hora_y_los_booleanos_en_espanol()
    {
        using var e = new Escenario(bd); // el reloj simulado usa UTC como hora local

        var texto = Texto(e.Exportador.ACsv(Tabla(Utc, new SoloFecha(Utc), new SoloFecha(null), true, false, null)));

        Assert.EndsWith("\r\n10/11/2026 14:30,10/11/2026,,Sí,No,\r\n", texto);
    }

    // ---------- Excel ----------

    private static XLWorkbook Abrir(byte[] xlsx) => new(new MemoryStream(xlsx));

    [Fact]
    public void El_excel_trae_encabezados_en_negritas_fila_congelada_y_los_datos()
    {
        using var e = new Escenario(bd);

        using var libro = Abrir(e.Exportador.AXlsx(new TablaExportable("Reporte", ["Código", "Días"], [["PRO-1", -3], ["PRO-2", 5]])));
        var hoja = libro.Worksheet(1);

        Assert.Equal("Reporte", hoja.Name);
        Assert.Equal("Código", hoja.Cell(1, 1).GetString());
        Assert.True(hoja.Cell(1, 1).Style.Font.Bold);
        Assert.Equal("PRO-1", hoja.Cell(2, 1).GetString());
        Assert.Equal(-3, hoja.Cell(2, 2).GetDouble());   // número de verdad, no texto
        Assert.Equal(5, hoja.Cell(3, 2).GetDouble());
        Assert.Equal(1, hoja.SheetView.SplitRow);
    }

    [Fact]
    public void El_excel_nunca_interpreta_un_texto_como_formula()
    {
        using var e = new Escenario(bd);

        using var libro = Abrir(e.Exportador.AXlsx(Tabla("=1+1", "+SUM(A1)", "@x")));
        var hoja = libro.Worksheet(1);

        foreach (var col in new[] { 1, 2, 3 })
        {
            Assert.False(hoja.Cell(2, col).HasFormula);
            Assert.Equal(XLDataType.Text, hoja.Cell(2, col).DataType);
        }

        Assert.Equal("=1+1", hoja.Cell(2, 1).GetString());
    }

    [Fact]
    public void El_excel_guarda_las_fechas_como_fechas_y_los_booleanos_en_espanol()
    {
        using var e = new Escenario(bd);

        using var libro = Abrir(e.Exportador.AXlsx(Tabla(Utc, new SoloFecha(Utc), true, null)));
        var hoja = libro.Worksheet(1);

        Assert.Equal(XLDataType.DateTime, hoja.Cell(2, 1).DataType);
        Assert.Equal(new DateTime(2026, 11, 10, 14, 30, 0), hoja.Cell(2, 1).GetDateTime());
        Assert.Equal(new DateTime(2026, 11, 10), hoja.Cell(2, 2).GetDateTime());
        Assert.Equal("Sí", hoja.Cell(2, 3).GetString());
        Assert.True(hoja.Cell(2, 4).IsEmpty());
    }

    [Theory]
    [InlineData("Historial PRO/CAL:001?", "Historial PROCAL001")]   // caracteres que Excel no admite en el nombre de hoja
    [InlineData("", "Hoja")]
    public void El_nombre_de_la_hoja_se_limpia(string nombre, string esperado)
    {
        using var e = new Escenario(bd);

        using var libro = Abrir(e.Exportador.AXlsx(new TablaExportable(nombre, ["A"], [])));

        Assert.Equal(esperado, libro.Worksheet(1).Name);
    }

    [Fact]
    public void Un_nombre_de_hoja_demasiado_largo_se_recorta_al_limite_de_excel()
    {
        using var e = new Escenario(bd);

        using var libro = Abrir(e.Exportador.AXlsx(new TablaExportable(new string('H', 80), ["A"], [])));

        Assert.Equal(31, libro.Worksheet(1).Name.Length);
    }

    [Fact]
    public void Una_tabla_vacia_se_exporta_solo_con_encabezados()
    {
        using var e = new Escenario(bd);
        var tabla = new TablaExportable("Vacía", ["A", "B"], []);

        Assert.Equal("A,B\r\n", Texto(e.Exportador.ACsv(tabla)));
        using var libro = Abrir(e.Exportador.AXlsx(tabla));
        Assert.Equal(1, libro.Worksheet(1).LastRowUsed()!.RowNumber());
    }
}
