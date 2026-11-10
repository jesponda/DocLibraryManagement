using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace DocLibraryManagement.Tests.Correo;

/// <summary>
/// Los remitentes reales (carpeta .eml y SMTP con MailKit), resueltos como en producción: por la configuración.
/// El SMTP se prueba contra un servidor de verdad en el equipo, no contra un doble.
/// </summary>
public class SendersTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static Dictionary<string, string?> ConSmtp(int puerto) => new()
    {
        ["Correo:Modo"] = "Smtp",
        ["Correo:Servidor"] = "127.0.0.1",
        ["Correo:Puerto"] = puerto.ToString(),
        ["Correo:Seguridad"] = "ninguna",
    };

    private static ICorreoSender Sender(Escenario e) => e.Scope().GetRequiredService<ICorreoSender>();

    private static readonly MensajeCorreo Muestra = new("destino@planta.test", "Ñandú Pérez", "Revisión técnica áéíóú", "Hola Ñandú,\nsegunda línea con acentos: ñ ü.");

    // ---------- Carpeta ----------

    [Fact]
    public async Task El_modo_carpeta_guarda_un_eml_legible_con_acentos_intactos()
    {
        using var e = new Escenario(bd, correo: true, remitenteReal: true);

        await Sender(e).EnviarAsync(Muestra);

        var archivo = Assert.Single(Directory.GetFiles(Path.Combine(e.RutaArchivos, "correo"), "*.eml"));
        var leido = await MimeMessage.LoadAsync(archivo);
        Assert.Equal("documentos@planta.test", leido.From.Mailboxes.Single().Address);
        var para = leido.To.Mailboxes.Single();
        Assert.Equal("destino@planta.test", para.Address);
        Assert.Equal("Ñandú Pérez", para.Name);
        Assert.Equal("Revisión técnica áéíóú", leido.Subject);
        Assert.Contains("segunda línea con acentos: ñ ü.", leido.TextBody);
    }

    [Fact]
    public async Task Cada_correo_va_a_su_propio_archivo()
    {
        using var e = new Escenario(bd, correo: true, remitenteReal: true);

        await Sender(e).EnviarAsync(Muestra);
        await Sender(e).EnviarAsync(Muestra);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(e.RutaArchivos, "correo"), "*.eml").Length);
    }

    [Theory]
    [InlineData("no-es-un-correo")]
    [InlineData("")]
    public async Task Un_remitente_mal_configurado_falla_con_un_mensaje_claro(string remitente)
    {
        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: new Dictionary<string, string?> { ["Correo:Remitente"] = remitente });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(e).EnviarAsync(Muestra));

        Assert.Contains("Correo:Remitente", ex.Message);
    }

    [Fact]
    public async Task Una_direccion_de_destino_con_salto_de_linea_no_puede_armar_un_mensaje()
    {
        using var e = new Escenario(bd, correo: true, remitenteReal: true);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            Sender(e).EnviarAsync(Muestra with { Para = "a@planta.test\r\nBcc: espia@externo.com" }));

        Assert.Empty(Directory.Exists(Path.Combine(e.RutaArchivos, "correo")) ? Directory.GetFiles(Path.Combine(e.RutaArchivos, "correo")) : []);
    }

    // ---------- SMTP real ----------

    [Fact]
    public async Task El_modo_smtp_entrega_el_mensaje_a_un_servidor_de_verdad()
    {
        await using var servidor = new ServidorSmtpDePrueba();
        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: ConSmtp(servidor.Puerto));

        await Sender(e).EnviarAsync(Muestra);

        var recibido = Assert.Single(servidor.Recibidos);
        Assert.Equal("documentos@planta.test", recibido.De);
        Assert.Equal(["destino@planta.test"], recibido.Para);
        using var datos = new MemoryStream(System.Text.Encoding.Latin1.GetBytes(recibido.Datos));
        var leido = await MimeMessage.LoadAsync(datos);
        Assert.Equal("Revisión técnica áéíóú", leido.Subject);
        Assert.Contains("segunda línea con acentos: ñ ü.", leido.TextBody);
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_al_destinatario_el_envio_falla()
    {
        await using var servidor = new ServidorSmtpDePrueba { RechazarDestinatario = true };
        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: ConSmtp(servidor.Puerto));

        await Assert.ThrowsAnyAsync<Exception>(() => Sender(e).EnviarAsync(Muestra));

        Assert.Empty(servidor.Recibidos);
    }

    [Fact]
    public async Task Si_no_hay_servidor_el_envio_falla_en_lugar_de_colgarse()
    {
        int puertoCerrado;
        await using (var temporal = new ServidorSmtpDePrueba())
        {
            puertoCerrado = temporal.Puerto;
        }

        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: ConSmtp(puertoCerrado));

        await Assert.ThrowsAnyAsync<Exception>(() => Sender(e).EnviarAsync(Muestra));
    }

    [Fact]
    public async Task El_modo_smtp_sin_servidor_configurado_lo_dice()
    {
        var extra = ConSmtp(25);
        extra["Correo:Servidor"] = "";
        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: extra);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Sender(e).EnviarAsync(Muestra));

        Assert.Contains("Correo:Servidor", ex.Message);
    }

    // ---------- De punta a punta ----------

    [Fact]
    public async Task Del_flujo_documental_al_servidor_smtp()
    {
        await using var servidor = new ServidorSmtpDePrueba();
        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: ConSmtp(servidor.Puerto));
        var f = await e.PrepararFlujoAsync();

        await e.IniciarAsync(f);
        await e.ProcesarColaAsync();

        var n = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo));
        Assert.Equal(EstadoNotificacion.Enviado, n.Estado);
        MimeMessage? mensaje = null;
        foreach (var r in servidor.Recibidos)
        {
            using var datos = new MemoryStream(System.Text.Encoding.Latin1.GetBytes(r.Datos));
            var candidato = await MimeMessage.LoadAsync(datos);
            if ((candidato.Subject ?? "").Contains(f.Documento.Codigo))
            {
                mensaje = candidato;
            }
        }

        var recibido = Assert.IsType<MimeMessage>(mensaje);
        Assert.Contains("Revisión técnica", recibido.TextBody);
        Assert.Contains($"https://docs.test/documentos/{f.Documento.Id}", recibido.TextBody);
    }

    [Fact]
    public async Task Si_el_servidor_smtp_esta_caido_la_notificacion_espera_y_no_se_pierde()
    {
        int puertoCerrado;
        await using (var temporal = new ServidorSmtpDePrueba())
        {
            puertoCerrado = temporal.Puerto;
        }

        using var e = new Escenario(bd, correo: true, remitenteReal: true, extra: ConSmtp(puertoCerrado));
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.ProcesarColaAsync();

        var n = Assert.Single(await e.NotificacionesDeAsync(f.Documento.Codigo));
        Assert.Equal(EstadoNotificacion.Pendiente, n.Estado);
        Assert.Equal(1, n.Intentos);
        Assert.False(string.IsNullOrWhiteSpace(n.UltimoError));
    }
}
