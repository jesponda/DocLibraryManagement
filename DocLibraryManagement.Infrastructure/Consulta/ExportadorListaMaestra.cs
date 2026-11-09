using System.Text;
using ClosedXML.Excel;
using DocLibraryManagement.Application.Consulta;

namespace DocLibraryManagement.Infrastructure.Consulta;

internal sealed class ExportadorListaMaestra(TimeProvider reloj) : IExportadorListaMaestra
{
    private static readonly string[] Encabezados =
    [
        "Código", "Título", "Área", "Subárea", "Tipo", "Revisión",
        "Fecha de aprobación", "Vigente desde", "Próxima revisión", "Responsable",
    ];

    public byte[] ACsv(IReadOnlyList<FilaListaMaestra> filas)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Encabezados.Select(Celda))).Append("\r\n");

        foreach (var f in filas)
        {
            var celdas = new[]
            {
                f.Codigo, f.Titulo, f.Area, f.SubArea ?? "", f.Tipo, f.Revision,
                Fecha(f.FechaAprobacionUtc), Fecha(f.FechaVigenciaUtc), Fecha(f.FechaProximaRevisionUtc), f.Responsable,
            };
            sb.Append(string.Join(',', celdas.Select(Celda))).Append("\r\n");
        }

        // BOM UTF-8: sin él Excel abre el archivo en ANSI y los acentos salen rotos.
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return [.. utf8.GetPreamble(), .. utf8.GetBytes(sb.ToString())];
    }

    public byte[] AXlsx(IReadOnlyList<FilaListaMaestra> filas)
    {
        using var libro = new XLWorkbook();
        var hoja = libro.Worksheets.Add("Lista maestra");

        for (var c = 0; c < Encabezados.Length; c++)
        {
            var celda = hoja.Cell(1, c + 1);
            celda.Value = Encabezados[c];
            celda.Style.Font.Bold = true;
            celda.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        for (var i = 0; i < filas.Count; i++)
        {
            var f = filas[i];
            var r = i + 2;

            // Todo texto va como texto literal: un título que empiece con «=» nunca se interpreta como fórmula.
            Texto(hoja.Cell(r, 1), f.Codigo);
            Texto(hoja.Cell(r, 2), f.Titulo);
            Texto(hoja.Cell(r, 3), f.Area);
            Texto(hoja.Cell(r, 4), f.SubArea ?? "");
            Texto(hoja.Cell(r, 5), f.Tipo);
            Texto(hoja.Cell(r, 6), f.Revision);
            FechaXlsx(hoja.Cell(r, 7), f.FechaAprobacionUtc);
            FechaXlsx(hoja.Cell(r, 8), f.FechaVigenciaUtc);
            FechaXlsx(hoja.Cell(r, 9), f.FechaProximaRevisionUtc);
            Texto(hoja.Cell(r, 10), f.Responsable);
        }

        // Anchos fijos: el ajuste automático depende de las fuentes instaladas en el servidor.
        double[] anchos = [16, 48, 28, 20, 22, 10, 18, 16, 18, 26];
        for (var c = 0; c < anchos.Length; c++)
        {
            hoja.Column(c + 1).Width = anchos[c];
        }

        hoja.SheetView.FreezeRows(1);

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    // ---------- Apoyo ----------

    private DateTime? Local(DateTime? utc) =>
        utc is null ? null : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), reloj.LocalTimeZone);

    private string Fecha(DateTime? utc) => Local(utc)?.ToString("dd/MM/yyyy") ?? "";

    private void FechaXlsx(IXLCell celda, DateTime? utc)
    {
        if (Local(utc) is { } local)
        {
            celda.Value = local.Date;
            celda.Style.DateFormat.Format = "dd/mm/yyyy";
        }
    }

    // Se asigna como texto (XLCellValue.FromObject(string)); las fórmulas solo existen vía FormulaA1, que aquí nunca se usa.
    private static void Texto(IXLCell celda, string valor) => celda.Value = valor;

    /// <summary>
    /// Celda CSV según RFC 4180. Además neutraliza la «inyección de fórmulas»: Excel ejecuta como fórmula
    /// cualquier celda que empiece con = + - @ (o tabulador / retorno), así que se antepone un apóstrofo.
    /// </summary>
    internal static string Celda(string? valor)
    {
        var texto = valor ?? "";

        if (texto.Length > 0 && texto[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            texto = "'" + texto;
        }

        return texto.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + texto.Replace("\"", "\"\"") + "\""
            : texto;
    }
}
