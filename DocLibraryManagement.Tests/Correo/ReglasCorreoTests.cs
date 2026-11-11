using DocLibraryManagement.Application.Correo;

namespace DocLibraryManagement.Tests.Correo;

public class ReglasCorreoTests
{
    // ---------- Direcciones ----------

    [Theory]
    [InlineData("juan@empresa.com", true)]
    [InlineData("juan.perez@planta.empresa.com.mx", true)]
    [InlineData("juan+docs@empresa.com", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("juan", false)]
    [InlineData("juan@", false)]
    [InlineData("@empresa.com", false)]
    [InlineData("juan@localhost", false)]               // sin punto en el dominio: no entregable fuera
    [InlineData("juan perez@empresa.com", false)]       // con espacio
    [InlineData("Juan Pérez <juan@empresa.com>", false)] // formato "nombre <dirección>": se exige la dirección sola
    [InlineData("juan@empresa.com\r\nBcc: espia@x.com", false)] // inyección de cabeceras
    [InlineData("juan@empresa.com\nBcc: espia@x.com", false)]
    public void Solo_se_aceptan_direcciones_simples_de_una_linea(string? direccion, bool valida)
    {
        Assert.Equal(valida, ResolutorCorreo.EsDireccionValida(direccion));
    }

    [Fact]
    public void Una_direccion_demasiado_larga_se_rechaza()
    {
        var larga = new string('a', ResolutorCorreo.LongitudMaxima) + "@empresa.com";

        Assert.False(ResolutorCorreo.EsDireccionValida(larga));
    }

    [Theory]
    [InlineData(@"PLANTA\jperez", "jperez")]
    [InlineData(@"DOM\SUB\jperez", "jperez")]
    [InlineData("jperez@planta.local", "jperez")]
    [InlineData("jperez", "jperez")]
    [InlineData(@"  PLANTA\jperez  ", "jperez")]
    public void El_nombre_de_usuario_se_obtiene_sin_dominio(string identificador, string esperado)
    {
        Assert.Equal(esperado, ResolutorCorreo.NombreDeUsuario(identificador));
    }

    // ---------- Correo efectivo ----------

    [Fact]
    public void Un_correo_capturado_manda_sobre_la_convencion()
    {
        var r = ResolutorCorreo.Efectivo("especial@otra.com", @"PLANTA\jperez", "{usuario}@empresa.com");

        Assert.Equal("especial@otra.com", r.Direccion);
        Assert.Equal(OrigenCorreo.Explicito, r.Origen);
    }

    [Fact]
    public void Sin_correo_capturado_se_arma_con_la_convencion_en_minusculas()
    {
        var r = ResolutorCorreo.Efectivo(null, @"PLANTA\JPerez", "{usuario}@empresa.com");

        Assert.Equal("jperez@empresa.com", r.Direccion);
        Assert.Equal(OrigenCorreo.Convencion, r.Origen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sin-marcador@empresa.com")] // el patrón no tiene {usuario}: no es una convención
    public void Sin_convencion_aplicable_y_sin_correo_no_hay_direccion(string? patron)
    {
        var r = ResolutorCorreo.Efectivo(null, @"PLANTA\jperez", patron);

        Assert.Null(r.Direccion);
        Assert.Equal(OrigenCorreo.SinCorreo, r.Origen);
    }

    [Fact]
    public void Un_correo_capturado_invalido_se_ignora_y_cae_a_la_convencion()
    {
        var r = ResolutorCorreo.Efectivo("esto no es un correo", @"PLANTA\jperez", "{usuario}@empresa.com");

        Assert.Equal("jperez@empresa.com", r.Direccion);
        Assert.Equal(OrigenCorreo.Convencion, r.Origen);
    }

    [Fact]
    public void Si_la_convencion_produce_una_direccion_invalida_no_se_envia_a_ningun_lado()
    {
        // Un nombre de usuario con espacio no forma una dirección válida: mejor «sin correo» que un correo mal dirigido.
        var r = ResolutorCorreo.Efectivo(null, @"PLANTA\juan perez", "{usuario}@empresa.com");

        Assert.Null(r.Direccion);
        Assert.Equal(OrigenCorreo.SinCorreo, r.Origen);
    }

    // ---------- Plantillas ----------

    private static readonly IReadOnlyDictionary<string, PlantillaOpciones> SinConfiguracion =
        new Dictionary<string, PlantillaOpciones>();

    // Recorre TODOS los eventos declarados: un evento nuevo sin plantilla (o sin enlace) hace fallar esta prueba.
    public static TheoryData<string> TodosLosEventos => [.. EventosCorreo.Todos];

    [Theory]
    [MemberData(nameof(TodosLosEventos))]
    public void Cada_evento_tiene_plantilla_con_los_datos_para_actuar(string evento)
    {
        var p = PlantillasCorreo.Predeterminada(evento);

        Assert.Contains("{Codigo}", p.Asunto);
        Assert.Contains("{Destinatario}", p.Cuerpo);
        Assert.Contains("{Codigo}", p.Cuerpo);
        Assert.Contains("{Enlace}", p.Cuerpo);
    }

    [Fact]
    public void Un_evento_desconocido_falla_fuerte_en_vez_de_mandar_un_correo_vacio()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PlantillasCorreo.Predeterminada("Inventado"));
    }

    [Fact]
    public void Se_reemplazan_los_valores_en_asunto_y_cuerpo()
    {
        var p = new Plantilla("Aviso {Codigo}", "Hola {Destinatario}, revisa {Codigo} en {Enlace}");
        var valores = new Dictionary<string, string> { ["Codigo"] = "PRO-CAL-001", ["Destinatario"] = "Rosa", ["Enlace"] = "https://x/y" };

        var r = PlantillasCorreo.Renderizar(p, valores);

        Assert.Equal("Aviso PRO-CAL-001", r.Asunto);
        Assert.Equal("Hola Rosa, revisa PRO-CAL-001 en https://x/y", r.Cuerpo);
    }

    [Fact]
    public void Un_marcador_desconocido_se_deja_visible_en_lugar_de_desaparecer()
    {
        var r = PlantillasCorreo.Renderizar(new Plantilla("{Codigo} {NoExiste}", "x"), new Dictionary<string, string> { ["Codigo"] = "A" });

        Assert.Equal("A {NoExiste}", r.Asunto); // un error de configuración se nota en el correo, no se oculta
    }

    [Fact]
    public void Los_saltos_de_linea_en_un_valor_no_pueden_partir_el_asunto_ni_inyectar_cabeceras()
    {
        var valores = new Dictionary<string, string> { ["Titulo"] = "Normal\r\nBcc: espia@x.com\nOtra: cosa\ttab" };

        var r = PlantillasCorreo.Renderizar(new Plantilla("Documento {Titulo}", "Título: {Titulo}"), valores);

        Assert.DoesNotContain('\r', r.Asunto);
        Assert.DoesNotContain('\n', r.Asunto);
        Assert.DoesNotContain('\t', r.Asunto);
        Assert.Contains("Bcc: espia@x.com", r.Asunto); // el texto queda, pero ya dentro de la misma línea del asunto
        Assert.Contains('\n', r.Cuerpo);               // en el cuerpo los saltos sí se respetan
    }

    [Fact]
    public void Un_asunto_muy_largo_se_recorta()
    {
        var r = PlantillasCorreo.Renderizar(new Plantilla("{T}", "x"), new Dictionary<string, string> { ["T"] = new string('a', 500) });

        Assert.Equal(PlantillasCorreo.MaximoAsunto, r.Asunto.Length);
        Assert.EndsWith("…", r.Asunto);
    }

    [Fact]
    public void Los_espacios_repetidos_del_asunto_se_normalizan()
    {
        var r = PlantillasCorreo.Renderizar(new Plantilla("  A   {X}  ", "x"), new Dictionary<string, string> { ["X"] = "  B " });

        Assert.Equal("A B", r.Asunto);
    }

    [Fact]
    public void La_configuracion_reemplaza_asunto_y_cuerpo_por_separado()
    {
        var predeterminada = PlantillasCorreo.Predeterminada(EventosCorreo.Rechazada);
        var soloAsunto = new Dictionary<string, PlantillaOpciones> { ["Rechazada"] = new() { Asunto = "Mi asunto" } };
        var soloCuerpo = new Dictionary<string, PlantillaOpciones> { ["rechazada"] = new() { Cuerpo = "Mi cuerpo" } }; // sin distinguir mayúsculas
        var comparador = new Dictionary<string, PlantillaOpciones>(soloCuerpo, StringComparer.OrdinalIgnoreCase);

        var a = PlantillasCorreo.Obtener(EventosCorreo.Rechazada, soloAsunto);
        var c = PlantillasCorreo.Obtener(EventosCorreo.Rechazada, comparador);

        Assert.Equal("Mi asunto", a.Asunto);
        Assert.Equal(predeterminada.Cuerpo, a.Cuerpo);     // lo que no se reemplaza conserva el predeterminado
        Assert.Equal(predeterminada.Asunto, c.Asunto);
        Assert.Equal("Mi cuerpo", c.Cuerpo);
    }

    [Fact]
    public void Sin_configuracion_se_usa_la_plantilla_predeterminada()
    {
        Assert.Equal(PlantillasCorreo.Predeterminada(EventosCorreo.Publicada), PlantillasCorreo.Obtener(EventosCorreo.Publicada, SinConfiguracion));
    }

    [Fact]
    public void Una_plantilla_configurada_en_blanco_no_borra_el_texto_predeterminado()
    {
        var cfg = new Dictionary<string, PlantillaOpciones> { ["Publicada"] = new() { Asunto = "   ", Cuerpo = "" } };

        Assert.Equal(PlantillasCorreo.Predeterminada(EventosCorreo.Publicada), PlantillasCorreo.Obtener(EventosCorreo.Publicada, cfg));
    }
}
