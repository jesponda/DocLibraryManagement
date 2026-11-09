using System.Globalization;
using System.Text;
using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Consulta;

/// <summary>Reglas puras de la búsqueda: cómo se parten los términos y cómo se arma el fragmento con coincidencias resaltadas.</summary>
public static class ReglasBusqueda
{
    public const int LongitudMinimaTermino = 2;
    public const int MaximoTerminos = 8;
    public const int MaximoLongitudConsulta = 200;
    public const int MaximoFilasExportacion = 5000;
    public const int TamanoMaximoPagina = 100;

    /// <summary>
    /// Parte el texto en palabras. Descarta repetidas (sin distinguir mayúsculas ni acentos) y las de un solo
    /// carácter, que coincidirían con casi todo. Exige que quede al menos una.
    /// </summary>
    public static IReadOnlyList<string> Terminos(string? texto)
    {
        var limpio = (texto ?? "").Trim();
        if (limpio.Length > MaximoLongitudConsulta)
        {
            throw new ReglaNegocioException($"La búsqueda no puede pasar de {MaximoLongitudConsulta} caracteres.");
        }

        var terminos = new List<string>();
        foreach (var palabra in limpio.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (palabra.Length < LongitudMinimaTermino)
            {
                continue;
            }

            if (terminos.Any(t => Iguales(t, palabra)))
            {
                continue;
            }

            terminos.Add(palabra);
            if (terminos.Count == MaximoTerminos)
            {
                break;
            }
        }

        if (terminos.Count == 0)
        {
            throw new ReglaNegocioException($"Escribe al menos una palabra de {LongitudMinimaTermino} o más caracteres.");
        }

        return terminos;
    }

    /// <summary>Escapa los comodines de LIKE para que el texto del usuario se busque literal («100%» no es un comodín).</summary>
    public static string EscaparLike(string termino) =>
        termino.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    public static (int Pagina, int Tamano) NormalizarPagina(int pagina, int tamano, int tamanoPorDefecto)
    {
        var t = tamano <= 0 ? tamanoPorDefecto : Math.Min(tamano, TamanoMaximoPagina);
        return (Math.Max(1, pagina), t);
    }

    private static bool Iguales(string a, string b) =>
        CultureInfo.InvariantCulture.CompareInfo.Compare(a, b, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0;

    // ---------- Fragmento con resaltado ----------

    /// <summary>
    /// Recorta alrededor de la primera coincidencia y marca todas las que caigan en el recorte. Compara sin
    /// distinguir mayúsculas ni acentos, igual que la consulta. Devuelve segmentos (no HTML): quien los pinte
    /// decide cómo resaltarlos, y el texto del PDF nunca se interpreta como marcado.
    /// </summary>
    public static IReadOnlyList<FragmentoTexto> Fragmento(string? texto, IReadOnlyList<string> terminos, int ancho = 220)
    {
        if (string.IsNullOrWhiteSpace(texto) || terminos.Count == 0)
        {
            return [];
        }

        var primera = SiguienteCoincidencia(texto, terminos, 0);
        if (primera is null)
        {
            return [];
        }

        // Ventana: un poco de contexto antes de la coincidencia y el resto después, sin cortar palabras.
        var inicio = Math.Max(0, primera.Value.Indice - ancho / 4);
        var fin = Math.Min(texto.Length, inicio + ancho);
        if (primera.Value.Indice + primera.Value.Longitud > fin)
        {
            fin = Math.Min(texto.Length, primera.Value.Indice + primera.Value.Longitud + ancho / 4);
        }

        inicio = AjustarAInicioDePalabra(texto, inicio);
        fin = AjustarAFinDePalabra(texto, fin);

        var segmentos = new List<FragmentoTexto>();
        if (inicio > 0)
        {
            segmentos.Add(new FragmentoTexto("…", false));
        }

        var ventana = texto[inicio..fin];
        var cursor = 0;
        while (SiguienteCoincidencia(ventana, terminos, cursor) is { } m)
        {
            if (m.Indice > cursor)
            {
                Agregar(segmentos, ventana[cursor..m.Indice], false);
            }

            Agregar(segmentos, ventana.Substring(m.Indice, m.Longitud), true);
            cursor = m.Indice + m.Longitud;
        }

        if (cursor < ventana.Length)
        {
            Agregar(segmentos, ventana[cursor..], false);
        }

        if (fin < texto.Length)
        {
            segmentos.Add(new FragmentoTexto("…", false));
        }

        return segmentos;
    }

    private static (int Indice, int Longitud)? SiguienteCoincidencia(string texto, IReadOnlyList<string> terminos, int desde)
    {
        (int Indice, int Longitud)? mejor = null;
        var comparador = CultureInfo.InvariantCulture.CompareInfo;

        foreach (var termino in terminos)
        {
            var indice = comparador.IndexOf(texto.AsSpan(desde), termino.AsSpan(),
                CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var longitud);
            if (indice < 0 || longitud == 0)
            {
                continue;
            }

            indice += desde;
            if (mejor is null || indice < mejor.Value.Indice || (indice == mejor.Value.Indice && longitud > mejor.Value.Longitud))
            {
                mejor = (indice, longitud);
            }
        }

        return mejor;
    }

    private static int AjustarAInicioDePalabra(string texto, int inicio)
    {
        if (inicio == 0)
        {
            return 0;
        }

        // Se avanza hasta el siguiente espacio (máximo unos caracteres) para no empezar a media palabra.
        var tope = Math.Min(texto.Length, inicio + 20);
        for (var i = inicio; i < tope; i++)
        {
            if (char.IsWhiteSpace(texto[i]))
            {
                return i + 1;
            }
        }

        return inicio;
    }

    private static int AjustarAFinDePalabra(string texto, int fin)
    {
        if (fin >= texto.Length)
        {
            return texto.Length;
        }

        var tope = Math.Min(texto.Length, fin + 20);
        for (var i = fin; i < tope; i++)
        {
            if (char.IsWhiteSpace(texto[i]))
            {
                return i;
            }
        }

        return fin;
    }

    private static void Agregar(List<FragmentoTexto> lista, string texto, bool resaltado)
    {
        if (texto.Length == 0)
        {
            return;
        }

        if (lista.Count > 0 && lista[^1].Resaltado == resaltado)
        {
            lista[^1] = new FragmentoTexto(lista[^1].Texto + texto, resaltado);
        }
        else
        {
            lista.Add(new FragmentoTexto(texto, resaltado));
        }
    }
}
