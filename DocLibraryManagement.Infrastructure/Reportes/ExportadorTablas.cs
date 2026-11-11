using System.Text;
using ClosedXML.Excel;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Infrastructure.Consulta;

namespace DocLibraryManagement.Infrastructure.Reportes;

internal sealed class ExportadorTablas(TimeProvider reloj) : IExportadorTablas
{
    private const int LongitudMaximaNombreHoja = 31; // límite de Excel
    private static readonly char[] CaracteresInvalidosHoja = [':', '\\', '/', '?', '*', '[', ']'];

    public byte[] ACsv(TablaExportable tabla)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', tabla.Encabezados.Select(ExportadorListaMaestra.Celda))).Append("\r\n");

        foreach (var fila in tabla.Filas)
        {
            sb.Append(string.Join(',', fila.Select(CeldaCsv))).Append("\r\n");
        }

        // BOM UTF-8: sin él Excel abre el archivo en ANSI y los acentos salen rotos.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. utf8.GetPreamble(), .. utf8.GetBytes(sb.ToString())];
    }

    public byte[] AXlsx(TablaExportable tabla)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add(NombreHoja(tabla.NombreHoja));

        for (var c = 0; c < tabla.Encabezados.Count; c++)
        {
            var celda = hoja.Cell(1, c + 1);
            celda.Value = tabla.Encabezados[c];
            celda.Style.Font.Bold = true;
            celda.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (var i = 0; i < tabla.Filas.Count; i++)
        {
            var fila = tabla.Filas[i];
            for (var c = 0; c < fila.Count; c++)
            {
                Escribir(hoja.Cell(i + 2, c + 1), fila[c]);
            }
        }

        // Anchos fijos: el ajuste automático depende de las fuentes instaladas en el servidor.
        for (var c = 0; c < tabla.Encabezados.Count; c++)
        {
            hoja.Column(c + 1).Width = 22;
        }

        hoja.SheetView.FreezeRows(1);

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    // ---------- Apoyo ----------

    private DateTime Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), reloj.LocalTimeZone);

    /// <summary>
    /// Los números van tal cual (un «-3» de días es un número, no una fórmula); todo lo demás es texto y pasa por la
    /// protección contra inyección de fórmulas.
    /// </summary>
    private string CeldaCsv(object? valor) =>
        valor is int or long
            ? TextoCsv(valor)
            : ExportadorListaMaestra.Celda(TextoCsv(valor));

    private string TextoCsv(object? valor) => valor switch
    {
        null => "",
        string s => s,
        SoloFecha { Utc: null } => "",
        SoloFecha f => Local(f.Utc!.Value).ToString("dd/MM/yyyy"),
        DateTime d => Local(d).ToString("dd/MM/yyyy HH:mm"),
        bool b => b ? "Sí" : "No",
        IFormattable n => n.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => valor.ToString() ?? "",
    };

    private void Escribir(IXLCell celda, object? valor)
    {
        switch (valor)
        {
            case null:
                break;
            case string s:
                celda.Value = s; // siempre como texto literal: nunca se interpreta como fórmula
                break;
            case SoloFecha { Utc: { } utc }:
                celda.Value = Local(utc).Date;
                celda.Style.DateFormat.Format = "dd/mm/yyyy";
                break;
            case SoloFecha:
                break;
            case DateTime d:
                celda.Value = Local(d);
                celda.Style.DateFormat.Format = "dd/mm/yyyy hh:mm";
                break;
            case bool b:
                celda.Value = b ? "Sí" : "No";
                break;
            case int n:
                celda.Value = n;
                break;
            case long n:
                celda.Value = n;
                break;
            default:
                celda.Value = valor.ToString() ?? "";
                break;
        }
    }

    private static string NombreHoja(string nombre)
    {
        var limpio = string.Concat(nombre.Where(ch => !CaracteresInvalidosHoja.Contains(ch)));
        if (limpio.Length == 0)
        {
            limpio = "Hoja";
        }

        return limpio.Length <= LongitudMaximaNombreHoja ? limpio : limpio[..LongitudMaximaNombreHoja];
    }
}
