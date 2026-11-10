using DocLibraryManagement.Application.Correo;
using MailKit; // SendAsync(MimeMessage, …) es un método de extensión de este espacio de nombres (MailKit 5)
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace DocLibraryManagement.Infrastructure.Correo;

internal static class ConstructorMensajes
{
    /// <summary>
    /// Arma el mensaje MIME. MimeKit codifica por sí mismo los acentos y rechaza saltos de línea en direcciones, y el
    /// asunto llega ya en una sola línea (ver <see cref="PlantillasCorreo.Renderizar"/>).
    /// </summary>
    public static MimeMessage Crear(CorreoOptions o, MensajeCorreo mensaje)
    {
        if (!ResolutorCorreo.EsDireccionValida(o.Remitente))
        {
            throw new InvalidOperationException("La opción 'Correo:Remitente' no es una dirección de correo válida.");
        }

        var correo = new MimeMessage();
        correo.From.Add(new MailboxAddress(o.NombreRemitente, o.Remitente));
        correo.To.Add(new MailboxAddress(mensaje.NombrePara ?? "", mensaje.Para));
        correo.Subject = mensaje.Asunto;
        correo.Body = new TextPart("plain") { Text = mensaje.Cuerpo };
        return correo;
    }
}

/// <summary>Guarda cada correo como .eml en una carpeta. Pensado para desarrollo: se ven sin servidor SMTP.</summary>
internal sealed class CarpetaCorreoSender(IOptions<CorreoOptions> opciones, TimeProvider reloj) : ICorreoSender
{
    public async Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct = default)
    {
        var o = opciones.Value;
        if (string.IsNullOrWhiteSpace(o.CarpetaSalida))
        {
            throw new InvalidOperationException("Falta configurar 'Correo:CarpetaSalida' para el modo Carpeta.");
        }

        // Primero se arma el mensaje: si es inválido, no debe quedar un archivo vacío en la carpeta.
        var correo = ConstructorMensajes.Crear(o, mensaje);

        var carpeta = Path.GetFullPath(o.CarpetaSalida);
        Directory.CreateDirectory(carpeta);

        var ahora = reloj.GetUtcNow();
        var ruta = Path.Combine(carpeta, $"{ahora:yyyyMMdd-HHmmss}_{Guid.NewGuid():N}.eml");

        await using var archivo = File.Create(ruta);
        await correo.WriteToAsync(archivo, ct);
    }
}

internal sealed class SmtpCorreoSender(IOptions<CorreoOptions> opciones) : ICorreoSender
{
    private const int TiempoLimiteMs = 30_000;

    public async Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct = default)
    {
        var o = opciones.Value;
        if (string.IsNullOrWhiteSpace(o.Servidor))
        {
            throw new InvalidOperationException("Falta configurar 'Correo:Servidor' para el modo Smtp.");
        }

        var correo = ConstructorMensajes.Crear(o, mensaje);

        using var cliente = new SmtpClient { Timeout = TiempoLimiteMs };
        await cliente.ConnectAsync(o.Servidor, o.Puerto, Seguridad(o.Seguridad), ct);

        if (!string.IsNullOrEmpty(o.Usuario))
        {
            await cliente.AuthenticateAsync(o.Usuario, o.Clave, ct);
        }

        await cliente.SendAsync(correo, ct);
        await cliente.DisconnectAsync(quit: true, ct);
    }

    internal static SecureSocketOptions Seguridad(string valor) => (valor ?? "").Trim().ToLowerInvariant() switch
    {
        "ninguna" or "none" => SecureSocketOptions.None,
        "starttls" => SecureSocketOptions.StartTls,
        "ssl" or "sslonconnect" => SecureSocketOptions.SslOnConnect,
        _ => SecureSocketOptions.Auto,
    };
}
