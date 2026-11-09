using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;

namespace DocLibraryManagement.Tests.Consulta;

public class ReglasBusquedaTests
{
    // ---------- Términos ----------

    [Fact]
    public void Se_parte_en_palabras_ignorando_espacios_de_mas()
    {
        Assert.Equal(["bloqueo", "energía"], ReglasBusqueda.Terminos("  bloqueo    energía \t"));
    }

    [Fact]
    public void Las_palabras_repetidas_se_quitan_sin_distinguir_mayusculas_ni_acentos()
    {
        Assert.Equal(["Calibración"], ReglasBusqueda.Terminos("Calibración calibracion CALIBRACIÓN"));
    }

    [Fact]
    public void Las_palabras_de_un_solo_caracter_se_descartan_porque_coincidirian_con_casi_todo()
    {
        Assert.Equal(["energía"], ReglasBusqueda.Terminos("a energía y"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b c")]
    public void Sin_ninguna_palabra_valida_se_rechaza_con_un_mensaje(string? texto)
    {
        var ex = Assert.Throws<ReglaNegocioException>(() => ReglasBusqueda.Terminos(texto));
        Assert.Contains("al menos una palabra", ex.Message);
    }

    [Fact]
    public void Hay_un_maximo_de_terminos_para_acotar_el_costo_de_la_consulta()
    {
        var muchas = string.Join(' ', Enumerable.Range(1, 20).Select(i => $"palabra{i}"));

        Assert.Equal(ReglasBusqueda.MaximoTerminos, ReglasBusqueda.Terminos(muchas).Count);
    }

    [Fact]
    public void Una_consulta_demasiado_larga_se_rechaza()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasBusqueda.Terminos(new string('x', ReglasBusqueda.MaximoLongitudConsulta + 1)));
    }

    // ---------- Comodines ----------

    [Theory]
    [InlineData("100%", "100\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("normal", "normal")]
    public void Los_comodines_de_LIKE_se_escapan_para_buscarse_literales(string entrada, string esperado)
    {
        Assert.Equal(esperado, ReglasBusqueda.EscaparLike(entrada));
    }

    // ---------- Paginación ----------

    [Theory]
    [InlineData(0, 0, 20, 1, 20)]
    [InlineData(-5, -1, 20, 1, 20)]
    [InlineData(3, 10, 20, 3, 10)]
    [InlineData(1, 5000, 20, 1, ReglasBusqueda.TamanoMaximoPagina)]
    public void La_paginacion_se_normaliza_a_valores_razonables(int pagina, int tamano, int defecto, int paginaEsperada, int tamanoEsperado)
    {
        Assert.Equal((paginaEsperada, tamanoEsperado), ReglasBusqueda.NormalizarPagina(pagina, tamano, defecto));
    }

    [Theory]
    [InlineData(0, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(100, 20, 5)]
    public void El_total_de_paginas_se_calcula_hacia_arriba(int total, int tamano, int esperadas)
    {
        Assert.Equal(esperadas, new Pagina<int>([], 1, tamano, total).TotalPaginas);
    }

    // ---------- Fragmento resaltado ----------

    private static string Plano(IReadOnlyList<FragmentoTexto> f) => string.Concat(f.Select(x => x.Texto));

    private static string[] Resaltados(IReadOnlyList<FragmentoTexto> f) => [.. f.Where(x => x.Resaltado).Select(x => x.Texto)];

    [Fact]
    public void Resalta_la_coincidencia_respetando_el_texto_original()
    {
        var f = ReglasBusqueda.Fragmento("Antes de calibrar verifique el CERO de la balanza.", ["cero"]);

        Assert.Equal(["CERO"], Resaltados(f)); // conserva las mayúsculas del documento, no las de la consulta
        Assert.Equal("Antes de calibrar verifique el CERO de la balanza.", Plano(f));
    }

    [Fact]
    public void Encuentra_sin_distinguir_acentos_en_ningun_sentido()
    {
        Assert.Equal(["calibración"], Resaltados(ReglasBusqueda.Fragmento("La calibración es anual.", ["calibracion"])));
        Assert.Equal(["calibracion"], Resaltados(ReglasBusqueda.Fragmento("La calibracion es anual.", ["calibración"])));
    }

    [Fact]
    public void Resalta_todas_las_palabras_buscadas_que_caen_en_el_fragmento()
    {
        var f = ReglasBusqueda.Fragmento("Aplicar el bloqueo y etiquetado de la energía eléctrica antes de intervenir.", ["energía", "bloqueo"]);

        Assert.Equal(["bloqueo", "energía"], Resaltados(f));
    }

    [Fact]
    public void Resalta_cada_aparicion_de_una_palabra()
    {
        var f = ReglasBusqueda.Fragmento("seguridad primero: la seguridad es de todos", ["seguridad"]);

        Assert.Equal(["seguridad", "seguridad"], Resaltados(f));
    }

    [Fact]
    public void En_un_texto_largo_recorta_alrededor_de_la_coincidencia_y_marca_los_cortes()
    {
        var texto = string.Concat(Enumerable.Repeat("relleno inicial sin interes ", 40))
                    + "AQUI esta la palabra clave buscada "
                    + string.Concat(Enumerable.Repeat("relleno final sin interes ", 40));

        var f = ReglasBusqueda.Fragmento(texto, ["clave"], ancho: 120);

        var plano = Plano(f);
        Assert.Equal(["clave"], Resaltados(f));
        Assert.StartsWith("…", plano);
        Assert.EndsWith("…", plano);
        Assert.True(plano.Length < 160, $"El fragmento debería ser corto pero mide {plano.Length}");
        Assert.Contains("palabra clave buscada", plano);
    }

    [Fact]
    public void No_corta_palabras_por_la_mitad_en_los_extremos()
    {
        var texto = string.Concat(Enumerable.Repeat("procedimiento ", 50)) + "diana " + string.Concat(Enumerable.Repeat("procedimiento ", 50));

        var plano = Plano(ReglasBusqueda.Fragmento(texto, ["diana"], ancho: 100)).Trim('…', ' ');

        Assert.All(plano.Split(' ', StringSplitOptions.RemoveEmptyEntries), palabra => Assert.True(palabra is "procedimiento" or "diana", $"Palabra cortada: «{palabra}»"));
    }

    [Fact]
    public void Una_coincidencia_al_inicio_no_lleva_puntos_suspensivos_antes()
    {
        var f = ReglasBusqueda.Fragmento("clave al principio " + new string('x', 5) + " " + string.Concat(Enumerable.Repeat("relleno ", 100)), ["clave"], ancho: 80);

        Assert.False(f[0].Texto.StartsWith('…'));
        Assert.EndsWith("…", Plano(f));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_texto_no_hay_fragmento(string? texto)
    {
        Assert.Empty(ReglasBusqueda.Fragmento(texto, ["algo"]));
    }

    [Fact]
    public void Si_la_palabra_no_esta_en_el_texto_no_hay_fragmento()
    {
        Assert.Empty(ReglasBusqueda.Fragmento("un texto cualquiera", ["inexistente"]));
    }

    [Fact]
    public void El_texto_del_PDF_nunca_se_interpreta_como_marcado()
    {
        // Se devuelven segmentos de texto plano: quien los pinte los codifica. Aquí solo se asegura que no se alteran.
        var f = ReglasBusqueda.Fragmento("peligro <script>alert(1)</script> aquí", ["peligro"]);

        Assert.Equal("peligro <script>alert(1)</script> aquí", Plano(f));
        Assert.Equal(["peligro"], Resaltados(f));
    }

    [Fact]
    public void Los_segmentos_contiguos_del_mismo_tipo_se_unen()
    {
        var f = ReglasBusqueda.Fragmento("uno dos tres cuatro", ["tres"]);

        Assert.Equal(3, f.Count); // antes, coincidencia, después
        Assert.False(f[0].Resaltado);
        Assert.True(f[1].Resaltado);
        Assert.False(f[2].Resaltado);
    }
}
