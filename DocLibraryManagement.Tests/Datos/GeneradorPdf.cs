using System.Text;

namespace DocLibraryManagement.Tests.Datos;

/// <summary>
/// Construye PDF mínimos pero VÁLIDOS (con tabla de referencias cruzadas correcta) para probar la lectura
/// de texto con un lector real. Cada texto es una página; admite acentos (codificación WinAnsi).
/// </summary>
public static class GeneradorPdf
{
    public static byte[] Crear(params string[] paginas)
    {
        if (paginas.Length == 0)
        {
            paginas = [""];
        }

        var salida = new MemoryStream();
        var posiciones = new List<long>();

        void Texto(string s) => salida.Write(Encoding.Latin1.GetBytes(s));

        void Objeto(string contenido)
        {
            posiciones.Add(salida.Position);
            Texto($"{posiciones.Count} 0 obj\n{contenido}\nendobj\n");
        }

        Texto("%PDF-1.4\n");

        // 1: catálogo, 2: páginas, 3: fuente; luego (página, contenido) por cada página: 4,5 / 6,7 / ...
        Objeto("<< /Type /Catalog /Pages 2 0 R >>");
        var hijos = string.Join(' ', Enumerable.Range(0, paginas.Length).Select(i => $"{4 + 2 * i} 0 R"));
        Objeto($"<< /Type /Pages /Kids [{hijos}] /Count {paginas.Length} >>");
        Objeto("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < paginas.Length; i++)
        {
            Objeto($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + 2 * i} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");

            // Una página sin texto es una página con contenido vacío (como un escaneo: solo imagen, ningún carácter).
            var flujo = string.IsNullOrEmpty(paginas[i])
                ? ""
                : $"BT /F1 12 Tf 72 720 Td ({Escapar(paginas[i])}) Tj ET";
            Objeto($"<< /Length {Encoding.Latin1.GetByteCount(flujo)} >>\nstream\n{flujo}\nendstream");
        }

        var inicioXref = salida.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {posiciones.Count + 1}\n0000000000 65535 f \n");
        foreach (var posicion in posiciones)
        {
            sb.Append($"{posicion:D10} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {posiciones.Count + 1} /Root 1 0 R >>\nstartxref\n{inicioXref}\n%%EOF\n");
        Texto(sb.ToString());

        return salida.ToArray();
    }

    private static string Escapar(string texto) =>
        texto.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
