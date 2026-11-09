using System.Text;
using DocLibraryManagement.Application.Consulta;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;

namespace DocLibraryManagement.Infrastructure.Consulta;

/// <summary>Lee el texto de un PDF con PdfPig (código abierto, solo .NET; no requiere instalar nada en el servidor).</summary>
internal sealed class ExtractorTextoPdf : IExtractorTexto
{
    /// <summary>Tope del texto guardado por PDF: acota el tamaño de la BD ante documentos enormes.</summary>
    internal const int MaximoCaracteres = 2_000_000;

    // La lectura es trabajo de CPU: se saca del hilo de la solicitud para no bloquear el circuito de Blazor.
    public Task<ExtraccionTexto> ExtraerAsync(Stream pdf, CancellationToken ct = default) =>
        Task.Run(() => Extraer(pdf, ct), ct);

    private static ExtraccionTexto Extraer(Stream pdf, CancellationToken ct)
    {
        try
        {
            using var documento = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });

            var crudo = new StringBuilder();
            foreach (var pagina in documento.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                crudo.Append(pagina.Text).Append(' ');
                if (crudo.Length >= MaximoCaracteres)
                {
                    break;
                }
            }

            return new ExtraccionTexto(Normalizar(crudo.ToString()), PudoLeerse: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Dañado, protegido con contraseña, versión no soportada… No es un error del sistema: se informa al autor.
            return new ExtraccionTexto("", PudoLeerse: false);
        }
    }

    /// <summary>Espacios en uno solo y sin caracteres de control, para que la búsqueda y los fragmentos sean estables.</summary>
    internal static string Normalizar(string texto)
    {
        var resultado = new StringBuilder(Math.Min(texto.Length, MaximoCaracteres));
        var espacioPendiente = false;

        foreach (var c in texto)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                espacioPendiente = resultado.Length > 0;
                continue;
            }

            if (espacioPendiente)
            {
                resultado.Append(' ');
                espacioPendiente = false;
            }

            resultado.Append(c);
            if (resultado.Length >= MaximoCaracteres)
            {
                break;
            }
        }

        return resultado.ToString();
    }
}
