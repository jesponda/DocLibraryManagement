using System.Net;
using System.Net.Sockets;
using System.Text;
using DocLibraryManagement.Application.Correo;

namespace DocLibraryManagement.Tests.Correo;

/// <summary>Remitente falso: registra lo "enviado" y puede fallar a voluntad. Seguro para uso concurrente.</summary>
public sealed class CorreoFalso : ICorreoSender
{
    private readonly object _candado = new();
    private readonly List<MensajeCorreo> _enviados = [];

    /// <summary>Si devuelve una excepción, el envío falla con ella (y no se registra).</summary>
    public Func<MensajeCorreo, Exception?>? Falla { get; set; }

    public IReadOnlyList<MensajeCorreo> Enviados
    {
        get
        {
            lock (_candado)
            {
                return [.. _enviados];
            }
        }
    }

    public int Intentos { get; private set; }

    public Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct = default)
    {
        lock (_candado)
        {
            Intentos++;
            if (Falla?.Invoke(mensaje) is { } error)
            {
                throw error;
            }

            _enviados.Add(mensaje);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Servidor SMTP mínimo de verdad (sin cifrado ni autenticación) para probar <c>SmtpCorreoSender</c> con MailKit
/// contra un servidor real y no solo contra un doble. Atiende una conexión a la vez.
/// </summary>
public sealed class ServidorSmtpDePrueba : IAsyncDisposable
{
    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _bucle;
    private readonly object _candado = new();
    private readonly List<CorreoRecibido> _recibidos = [];

    public ServidorSmtpDePrueba()
    {
        _escucha.Start();
        Puerto = ((IPEndPoint)_escucha.LocalEndpoint).Port;
        _bucle = Task.Run(AceptarAsync);
    }

    public int Puerto { get; }

    /// <summary>Si es true, rechaza al destinatario con un 550 (buzón inexistente).</summary>
    public bool RechazarDestinatario { get; set; }

    public IReadOnlyList<CorreoRecibido> Recibidos
    {
        get
        {
            lock (_candado)
            {
                return [.. _recibidos];
            }
        }
    }

    private async Task AceptarAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var cliente = await _escucha.AcceptTcpClientAsync(_cts.Token);
                await AtenderAsync(cliente);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Una conexión rota de una prueba no debe matar al servidor para las siguientes.
            }
        }
    }

    private async Task AtenderAsync(TcpClient cliente)
    {
        await using var flujo = cliente.GetStream();
        using var lector = new StreamReader(flujo, Encoding.Latin1);
        await using var escritor = new StreamWriter(flujo, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        string? de = null;
        var para = new List<string>();

        await escritor.WriteLineAsync("220 localhost ESMTP de prueba");
        while (await lector.ReadLineAsync(_cts.Token) is { } linea)
        {
            var comando = linea.ToUpperInvariant();
            if (comando.StartsWith("EHLO") || comando.StartsWith("HELO"))
            {
                await escritor.WriteLineAsync("250-localhost");
                await escritor.WriteLineAsync("250 8BITMIME");
            }
            else if (comando.StartsWith("MAIL FROM"))
            {
                de = Direccion(linea);
                para.Clear();
                await escritor.WriteLineAsync("250 OK");
            }
            else if (comando.StartsWith("RCPT TO"))
            {
                if (RechazarDestinatario)
                {
                    await escritor.WriteLineAsync("550 5.1.1 Buzon inexistente");
                }
                else
                {
                    para.Add(Direccion(linea));
                    await escritor.WriteLineAsync("250 OK");
                }
            }
            else if (comando.StartsWith("DATA"))
            {
                await escritor.WriteLineAsync("354 Termina con <CRLF>.<CRLF>");
                var datos = new StringBuilder();
                while (await lector.ReadLineAsync(_cts.Token) is { } l && l != ".")
                {
                    datos.AppendLine(l.StartsWith("..") ? l[1..] : l);
                }

                lock (_candado)
                {
                    _recibidos.Add(new CorreoRecibido(de ?? "", [.. para], datos.ToString()));
                }

                await escritor.WriteLineAsync("250 OK mensaje en cola");
            }
            else if (comando.StartsWith("QUIT"))
            {
                await escritor.WriteLineAsync("221 Hasta luego");
                return;
            }
            else if (comando.StartsWith("RSET") || comando.StartsWith("NOOP"))
            {
                await escritor.WriteLineAsync("250 OK");
            }
            else
            {
                await escritor.WriteLineAsync("502 Comando no implementado");
            }
        }
    }

    private static string Direccion(string linea)
    {
        var a = linea.IndexOf('<');
        var b = linea.IndexOf('>');
        return a >= 0 && b > a ? linea[(a + 1)..b] : linea;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _escucha.Stop();
        try
        {
            await _bucle;
        }
        catch (Exception)
        {
            // al apagar, cualquier excepción pendiente del bucle es irrelevante
        }

        _cts.Dispose();
    }
}

public sealed record CorreoRecibido(string De, IReadOnlyList<string> Para, string Datos);
