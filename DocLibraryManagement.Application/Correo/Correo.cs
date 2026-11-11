using System.Net.Mail;
using System.Text;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Correo;

public enum ModoCorreo
{
    /// <summary>No se encolan ni envían correos. Es el valor por defecto: nada sale sin configurarlo a propósito.</summary>
    Desactivado = 0,

    /// <summary>Guarda cada correo como archivo .eml en una carpeta. Para desarrollo: se pueden ver sin servidor SMTP.</summary>
    Carpeta = 1,

    Smtp = 2,
}

/// <summary>Opciones enlazadas desde la sección "Correo" de appsettings.</summary>
public class CorreoOptions
{
    public const string Seccion = "Correo";

    public ModoCorreo Modo { get; set; } = ModoCorreo.Desactivado;

    public string Servidor { get; set; } = "";

    public int Puerto { get; set; } = 25;

    /// <summary>Auto, Ninguna, StartTls o Ssl.</summary>
    public string Seguridad { get; set; } = "Auto";

    public string Usuario { get; set; } = "";

    /// <summary>No guardar en archivos del repositorio: usar variable de entorno o user secrets.</summary>
    public string Clave { get; set; } = "";

    public string Remitente { get; set; } = "";

    public string NombreRemitente { get; set; } = "Gestión documental";

    /// <summary>
    /// Cómo armar el correo de quien no tiene uno explícito. «{usuario}» se reemplaza por el nombre de usuario sin
    /// dominio (DOMINIO\\juan → juan), p. ej. «{usuario}@empresa.com». Vacío = solo cuentan los correos explícitos.
    /// </summary>
    public string PatronCorreo { get; set; } = "";

    /// <summary>Dirección pública del sistema, para los enlaces de los correos (p. ej. https://documentos.empresa.com).</summary>
    public string UrlBase { get; set; } = "";

    /// <summary>Carpeta de salida del modo Carpeta. Vacío = App_Data/correo junto a la aplicación.</summary>
    public string CarpetaSalida { get; set; } = "";

    public int MaximoIntentos { get; set; } = 5;

    /// <summary>Espera base entre reintentos; se duplica en cada intento (5, 10, 20, 40… minutos).</summary>
    public int MinutosReintento { get; set; } = 5;

    /// <summary>Cada cuántos segundos revisa la cola. 0 = no envía (los correos quedan en cola).</summary>
    public int SegundosEntreEnvios { get; set; } = 30;

    /// <summary>Cuánto tiempo «reserva» un proceso una notificación mientras la envía.</summary>
    public int MinutosArriendo { get; set; } = 10;

    /// <summary>Plantillas que reemplazan a las predeterminadas, por nombre de evento.</summary>
    public Dictionary<string, PlantillaOpciones> Plantillas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class PlantillaOpciones
{
    public string? Asunto { get; set; }

    public string? Cuerpo { get; set; }
}

/// <summary>Nombres de los eventos que generan correo (7.8).</summary>
public static class EventosCorreo
{
    public const string TareaAsignada = nameof(TareaAsignada);
    public const string Rechazada = nameof(Rechazada);
    public const string Publicada = nameof(Publicada);
    public const string AprobacionRevocada = nameof(AprobacionRevocada);
    public const string VigenciaReprogramada = nameof(VigenciaReprogramada);

    // Revisión periódica (7.4)
    public const string AlertaVencimiento = nameof(AlertaVencimiento);
    public const string DocumentoVencido = nameof(DocumentoVencido);
    public const string EscalamientoVencido = nameof(EscalamientoVencido);
    public const string SinCambiosSolicitada = nameof(SinCambiosSolicitada);
    public const string SinCambiosResuelta = nameof(SinCambiosResuelta);
    public const string VigenciaProxima = nameof(VigenciaProxima);

    // Cambio de responsable de un documento
    public const string ResponsableAsignado = nameof(ResponsableAsignado);
    public const string ResponsableRetirado = nameof(ResponsableRetirado);

    /// <summary>Todos los eventos con plantilla.</summary>
    public static IReadOnlyList<string> Todos { get; } =
    [
        TareaAsignada, Rechazada, Publicada, AprobacionRevocada, VigenciaReprogramada,
        AlertaVencimiento, DocumentoVencido, EscalamientoVencido, SinCambiosSolicitada, SinCambiosResuelta, VigenciaProxima,
        ResponsableAsignado, ResponsableRetirado,
    ];
}

public sealed record Plantilla(string Asunto, string Cuerpo);

public sealed record MensajeCorreo(string Para, string? NombrePara, string Asunto, string Cuerpo);

public static class PlantillasCorreo
{
    private const string Pie =
        "\n\n— Este es un mensaje automático del sistema de gestión documental. No respondas a este correo.";

    /// <summary>Plantillas predeterminadas. Se pueden sustituir una por una desde la configuración.</summary>
    public static Plantilla Predeterminada(string evento) => evento switch
    {
        EventosCorreo.TareaAsignada => new Plantilla(
            "[Gestión documental] Tienes una {TipoEtapa} pendiente: {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\nSe te asignó la etapa «{Etapa}» ({TipoEtapa}) del documento {Codigo} {Etiqueta} – {Titulo}.\n"
            + "Elaborada por: {Autor}.\n\nRevísala aquí: {Enlace}" + Pie),

        EventosCorreo.Rechazada => new Plantilla(
            "[Gestión documental] Rechazaron {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\n{Actor} rechazó la revisión {Codigo} {Etiqueta} – {Titulo} en la etapa «{Etapa}».\n\n"
            + "Motivo: {Motivo}\n\nLa revisión volvió a borrador. Corrígela y vuelve a enviarla: {Enlace}" + Pie),

        EventosCorreo.Publicada => new Plantilla(
            "[Gestión documental] Se publicó {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\nSe publicó {Codigo} {Etiqueta} – {Titulo}.\nEntra en vigor: {Fecha}.\n\n"
            + "Consúltala aquí: {Enlace}" + Pie),

        EventosCorreo.AprobacionRevocada => new Plantilla(
            "[Gestión documental] Anularon la aprobación de {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\n{Actor} anuló la aprobación de {Codigo} {Etiqueta} – {Titulo} antes de que entrara en vigor.\n\n"
            + "Motivo: {Motivo}\n\nLa revisión volvió a borrador. Corrígela y vuelve a enviarla por todo el flujo: {Enlace}" + Pie),

        EventosCorreo.VigenciaReprogramada => new Plantilla(
            "[Gestión documental] Cambió la fecha de vigencia de {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\n{Actor} cambió la fecha de entrada en vigor de {Codigo} {Etiqueta} – {Titulo}.\n"
            + "Nueva fecha: {Fecha}.\n\nMotivo: {Motivo}\n\nDetalle: {Enlace}" + Pie),

        EventosCorreo.AlertaVencimiento => new Plantilla(
            "[Gestión documental] La revisión de {Codigo} {Etiqueta} vence en {Dias} día(s)",
            "Hola {Destinatario},\n\nLa revisión periódica de {Codigo} {Etiqueta} – {Titulo} vence el {Fecha} (faltan {Dias} día(s)).\n\n"
            + "Revísalo: si sigue vigente tal cual, propón «Revisado sin cambios»; si requiere cambios, abre una revisión nueva.\n\n{Enlace}" + Pie),

        EventosCorreo.DocumentoVencido => new Plantilla(
            "[Gestión documental] Venció la revisión de {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\nLa revisión periódica de {Codigo} {Etiqueta} – {Titulo} venció el {Fecha}. El documento sigue vigente, "
            + "pero aparece como vencido en los reportes.\n\nPropón «Revisado sin cambios» o abre una revisión nueva: {Enlace}" + Pie),

        EventosCorreo.EscalamientoVencido => new Plantilla(
            "[Gestión documental] Documento vencido sin atender: {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\nLa revisión periódica de {Codigo} {Etiqueta} – {Titulo} venció el {Fecha} y su responsable ({Responsable}) "
            + "todavía no la atiende. Se te avisa por ser quien da seguimiento a esta área.\n\nDetalle: {Enlace}" + Pie),

        EventosCorreo.SinCambiosSolicitada => new Plantilla(
            "[Gestión documental] Pide tu decisión: {Codigo} {Etiqueta} revisado sin cambios",
            "Hola {Destinatario},\n\n{Actor} declara que revisó {Codigo} {Etiqueta} – {Titulo} y no requiere cambios. "
            + "La fecha de próxima revisión actual es {Fecha}.\n\nComentario: {Motivo}\n\nApruébalo o recházalo aquí: {Enlace}" + Pie),

        EventosCorreo.SinCambiosResuelta => new Plantilla(
            "[Gestión documental] {Actor} {Resultado} «revisado sin cambios» de {Codigo} {Etiqueta}",
            "Hola {Destinatario},\n\n{Actor} {Resultado} tu solicitud de «revisado sin cambios» de {Codigo} {Etiqueta} – {Titulo}.\n"
            + "{Detalle}\n\nComentario: {Motivo}\n\nDetalle: {Enlace}" + Pie),

        EventosCorreo.VigenciaProxima => new Plantilla(
            "[Gestión documental] {Codigo} {Etiqueta} entra en vigor el {Fecha}",
            "Hola {Destinatario},\n\n{Codigo} {Etiqueta} – {Titulo} ya está aprobada y entrará en vigor el {Fecha}. "
            + "Hasta entonces sigue siendo la vigente la revisión anterior.\n\nDetalle: {Enlace}" + Pie),

        EventosCorreo.ResponsableAsignado => new Plantilla(
            "[Gestión documental] Ahora eres responsable de {Codigo}",
            "Hola {Destinatario},\n\n{Actor} te asignó como responsable del documento {Codigo} – {Titulo}.\n"
            + "Desde ahora recibes sus avisos de revisión periódica y eres quien puede declararlo «Revisado sin cambios».\n\n"
            + "Motivo: {Motivo}\n\nDetalle: {Enlace}" + Pie),

        EventosCorreo.ResponsableRetirado => new Plantilla(
            "[Gestión documental] Ya no eres responsable de {Codigo}",
            "Hola {Destinatario},\n\n{Actor} cambió el responsable del documento {Codigo} – {Titulo}. "
            + "Ya no eres su responsable ni recibirás sus avisos de revisión periódica.\nNuevo responsable: {Responsable}.\n\n"
            + "Motivo: {Motivo}\n\nDetalle: {Enlace}" + Pie),

        _ => throw new ArgumentOutOfRangeException(nameof(evento), evento, "Evento de correo desconocido."),
    };

    /// <summary>La plantilla configurada (asunto y cuerpo por separado) o, si falta, la predeterminada.</summary>
    public static Plantilla Obtener(string evento, IReadOnlyDictionary<string, PlantillaOpciones> configuradas)
    {
        var baseDefecto = Predeterminada(evento);
        if (!configuradas.TryGetValue(evento, out var cfg))
        {
            return baseDefecto;
        }

        return new Plantilla(
            string.IsNullOrWhiteSpace(cfg.Asunto) ? baseDefecto.Asunto : cfg.Asunto,
            string.IsNullOrWhiteSpace(cfg.Cuerpo) ? baseDefecto.Cuerpo : cfg.Cuerpo);
    }

    public const int MaximoAsunto = 200;

    /// <summary>
    /// Reemplaza «{Nombre}» por su valor. Lo desconocido se deja tal cual (se nota en el correo). El asunto va en una
    /// sola línea: un salto de línea en un valor (p. ej. un título) permitiría inyectar cabeceras de correo.
    /// </summary>
    public static Plantilla Renderizar(Plantilla plantilla, IReadOnlyDictionary<string, string> valores)
    {
        string Sustituir(string texto, bool unaLinea)
        {
            var sb = new StringBuilder(texto);
            foreach (var (clave, valor) in valores)
            {
                sb.Replace("{" + clave + "}", unaLinea ? EnUnaLinea(valor) : valor);
            }

            return sb.ToString();
        }

        var asunto = EnUnaLinea(Sustituir(plantilla.Asunto, unaLinea: true));
        if (asunto.Length > MaximoAsunto)
        {
            asunto = asunto[..(MaximoAsunto - 1)] + "…";
        }

        return new Plantilla(asunto, Sustituir(plantilla.Cuerpo, unaLinea: false));
    }

    private static string EnUnaLinea(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            sb.Append(c is '\r' or '\n' or '\t' || char.IsControl(c) ? ' ' : c);
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

public enum OrigenCorreo
{
    /// <summary>El Administrador lo capturó (o se tomó de una fuente confiable).</summary>
    Explicito,

    /// <summary>Se armó con <see cref="CorreoOptions.PatronCorreo"/>.</summary>
    Convencion,

    SinCorreo,
}

public sealed record CorreoEfectivo(string? Direccion, OrigenCorreo Origen);

public static class ResolutorCorreo
{
    public const int LongitudMaxima = 256;

    /// <summary>
    /// Dirección válida y de una sola línea: sin espacios ni saltos (que permitirían inyectar cabeceras) y en la forma
    /// canónica usuario@dominio.
    /// </summary>
    public static bool EsDireccionValida(string? direccion)
    {
        if (string.IsNullOrWhiteSpace(direccion) || direccion.Length > LongitudMaxima)
        {
            return false;
        }

        if (direccion.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return false;
        }

        return MailAddress.TryCreate(direccion, out var m) && m.Address == direccion && m.Host.Contains('.');
    }

    /// <summary>«DOMINIO\juan» → «juan»; «juan@dominio.com» → «juan»; «juan» → «juan».</summary>
    public static string NombreDeUsuario(string identificador)
    {
        var s = identificador.Trim();
        var barra = s.LastIndexOf('\\');
        if (barra >= 0)
        {
            s = s[(barra + 1)..];
        }

        var arroba = s.IndexOf('@');
        return arroba > 0 ? s[..arroba] : s;
    }

    /// <summary>El correo explícito manda; si no hay, se arma con el patrón; si tampoco, no hay correo.</summary>
    public static CorreoEfectivo Efectivo(string? explicito, string identificador, string? patron)
    {
        if (EsDireccionValida(explicito))
        {
            return new CorreoEfectivo(explicito, OrigenCorreo.Explicito);
        }

        if (!string.IsNullOrWhiteSpace(patron) && patron.Contains("{usuario}", StringComparison.Ordinal))
        {
            var armado = patron.Replace("{usuario}", NombreDeUsuario(identificador).ToLowerInvariant(), StringComparison.Ordinal);
            if (EsDireccionValida(armado))
            {
                return new CorreoEfectivo(armado, OrigenCorreo.Convencion);
            }
        }

        return new CorreoEfectivo(null, OrigenCorreo.SinCorreo);
    }
}

// ---------- Contratos ----------

public sealed record NotificacionDto(
    long Id,
    string Tipo,
    string Destinatario,
    string? Correo,
    string Asunto,
    string? CodigoDocumento,
    EstadoNotificacion Estado,
    int Intentos,
    DateTime FechaCreacionUtc,
    DateTime? FechaEnvioUtc,
    DateTime ProximoIntentoUtc,
    string? UltimoError);

public sealed record UsuarioDto(
    Guid Id,
    string Identificador,
    string NombreVisible,
    bool Activo,
    string Roles,
    string? CorreoExplicito,
    string? CorreoEfectivo,
    OrigenCorreo Origen,
    DateTime UltimoAccesoUtc);

public interface ICorreoSender
{
    /// <summary>Entrega el mensaje o lanza una excepción. El llamador decide el reintento.</summary>
    Task EnviarAsync(MensajeCorreo mensaje, CancellationToken ct = default);
}

public interface IColaCorreoService
{
    /// <summary>
    /// Envía las notificaciones que ya les toca: las pendientes cuyo momento llegó y las que quedaron «Procesando» con el
    /// arriendo vencido. Cada una va en su propia unidad: que una falle no impide las demás. Devuelve cuántas se enviaron.
    /// </summary>
    Task<int> ProcesarPendientesAsync(int maximo = 50, CancellationToken ct = default);

    Task<Pagina<NotificacionDto>> ListarAsync(Actor actor, EstadoNotificacion? estado, int pagina = 1, int tamano = 25, CancellationToken ct = default);

    /// <summary>Vuelve a poner en cola una notificación en Error (solo Administrador).</summary>
    Task ReintentarAsync(Actor actor, long notificacionId, CancellationToken ct = default);
}

public interface IUsuariosService
{
    /// <summary>Solo Administrador. Incluye el correo efectivo y de dónde sale.</summary>
    Task<IReadOnlyList<UsuarioDto>> ListarAsync(Actor actor, CancellationToken ct = default);

    /// <summary>Fija el correo explícito de un usuario; <c>null</c> o vacío lo quita y vuelve a aplicar la convención. Solo Administrador.</summary>
    Task EstablecerCorreoAsync(Actor actor, Guid usuarioId, string? correo, CancellationToken ct = default);
}
