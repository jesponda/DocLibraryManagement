using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Correo;

/// <summary>
/// Pone un correo en la cola. NO guarda: agrega la notificación al contexto del llamador para que se confirme en
/// la MISMA transacción que el cambio de negocio (si el cambio se deshace, el correo tampoco existe).
/// </summary>
internal interface IEncoladorNotificaciones
{
    /// <param name="clave">Si se indica, no se encola de nuevo una notificación con la misma clave (avisos periódicos).</param>
    /// <param name="omitirIdentificador">No avisar a esta persona (p. ej. quien ejecutó la acción).</param>
    Task EncolarAsync(
        AppDbContext db,
        string evento,
        Guid destinatarioId,
        IReadOnlyDictionary<string, string> valores,
        string? codigoDocumento = null,
        string? clave = null,
        string? omitirIdentificador = null,
        CancellationToken ct = default);

    /// <summary>Valores comunes a todas las plantillas de un documento.</summary>
    Dictionary<string, string> ValoresBase(Documento documento, string etiqueta);

    /// <summary>Dirección completa dentro del sistema, o un texto de reemplazo si no hay <c>Correo:UrlBase</c>.</summary>
    string Enlace(string rutaRelativa);

    /// <summary>Fecha en la hora local del servidor, como la ve el usuario en las pantallas.</summary>
    string Fecha(DateTime utc);

    Task<string> NombreVisibleAsync(AppDbContext db, string identificador, CancellationToken ct = default);
}

internal sealed class EncoladorNotificaciones(IOptions<CorreoOptions> opciones, TimeProvider reloj) : IEncoladorNotificaciones
{
    public async Task EncolarAsync(
        AppDbContext db,
        string evento,
        Guid destinatarioId,
        IReadOnlyDictionary<string, string> valores,
        string? codigoDocumento = null,
        string? clave = null,
        string? omitirIdentificador = null,
        CancellationToken ct = default)
    {
        var o = opciones.Value;
        if (o.Modo == ModoCorreo.Desactivado)
        {
            return; // nada sale (ni se acumula en la cola) si el correo no se configuró a propósito
        }

        var usuario = await db.Usuarios.FindAsync([destinatarioId], ct);
        if (usuario is null || !usuario.Activo)
        {
            return;
        }

        if (omitirIdentificador is not null && usuario.Identificador.Equals(omitirIdentificador, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (clave is not null
            && (db.Notificaciones.Local.Any(n => n.Clave == clave) || await db.Notificaciones.AnyAsync(n => n.Clave == clave, ct)))
        {
            return; // ya se encoló antes: no se duplica
        }

        var todos = new Dictionary<string, string>(valores, StringComparer.Ordinal) { ["Destinatario"] = usuario.NombreVisible };
        var texto = PlantillasCorreo.Renderizar(PlantillasCorreo.Obtener(evento, o.Plantillas), todos);
        var ahora = reloj.GetUtcNow().UtcDateTime;

        db.Notificaciones.Add(new Notificacion
        {
            Tipo = evento,
            DestinatarioId = usuario.Id,
            Asunto = texto.Asunto,
            Cuerpo = texto.Cuerpo,
            CodigoDocumento = codigoDocumento,
            FechaCreacionUtc = ahora,
            ProximoIntentoUtc = ahora,
            Clave = clave,
        });
    }

    public Dictionary<string, string> ValoresBase(Documento documento, string etiqueta) => new(StringComparer.Ordinal)
    {
        ["Codigo"] = documento.Codigo,
        ["Titulo"] = documento.Titulo,
        ["Etiqueta"] = etiqueta,
        ["Enlace"] = Enlace($"documentos/{documento.Id}"),
        ["EnlaceVisor"] = Enlace($"doc/{documento.Codigo}"),
    };

    public string Enlace(string rutaRelativa)
    {
        var baseUrl = opciones.Value.UrlBase;
        return string.IsNullOrWhiteSpace(baseUrl)
            ? "(abre el sistema de gestión documental)"
            : baseUrl.TrimEnd('/') + "/" + rutaRelativa.TrimStart('/');
    }

    public string Fecha(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), reloj.LocalTimeZone).ToString("dd/MM/yyyy HH:mm");

    public async Task<string> NombreVisibleAsync(AppDbContext db, string identificador, CancellationToken ct = default)
    {
        if (identificador == Actor.IdentificadorSistema)
        {
            return "El sistema";
        }

        var nombre = await db.Usuarios.AsNoTracking()
            .Where(u => u.Identificador == identificador)
            .Select(u => u.NombreVisible)
            .FirstOrDefaultAsync(ct);

        return nombre ?? identificador;
    }
}
