using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Correo;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Documentos;

/// <summary>
/// La única puerta por la que una revisión pasa a Vigente. Centralizarla garantiza que SIEMPRE
/// la anterior quede Obsoleta en la misma transacción (7.3), sea cual sea el camino que llevó a publicar
/// (aprobación final inmediata o activación de una vigencia diferida).
/// </summary>
internal static class Publicacion
{
    /// <summary>
    /// Publica <paramref name="nueva"/> y guarda TODOS los cambios pendientes del contexto en una sola transacción.
    /// Son dos guardados porque el índice único «una sola vigente por documento» no admite que, a mitad de
    /// una sentencia, existan dos vigentes: primero se libera la anterior y después se promueve la nueva.
    /// </summary>
    public static async Task PublicarAsync(
        AppDbContext db,
        TimeProvider reloj,
        IEncoladorNotificaciones notificador,
        Actor actor,
        RevisionDocumento nueva,
        string codigoDocumento,
        int mesesRevision,
        DateTime vigenciaUtc,
        CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var anteriores = await db.Revisiones
            .Where(r => r.DocumentoId == nueva.DocumentoId && r.Estado == EstadoRevision.Vigente && r.Id != nueva.Id)
            .ToListAsync(ct);

        foreach (var anterior in anteriores)
        {
            anterior.Estado = EstadoRevision.Obsoleto;
            Bitacora.Registrar(db, reloj, actor, "Revision.Obsoletar", nameof(Documento), codigoDocumento,
                anterior: $"{anterior.Etiqueta} Vigente", nuevo: $"{anterior.Etiqueta} Obsoleto",
                comentario: $"Reemplazada por {nueva.Etiqueta}");
        }

        // Aquí también se guardan los cambios que el llamador dejó pendientes (la decisión, el flujo…).
        await db.SaveChangesAsync(ct);

        nueva.Estado = EstadoRevision.Vigente;
        nueva.FechaVigenciaUtc = vigenciaUtc;
        nueva.FechaProximaRevisionUtc = ReglasRevision.ProximaRevision(vigenciaUtc, mesesRevision);
        Bitacora.Registrar(db, reloj, actor, "Revision.Publicar", nameof(Documento), codigoDocumento,
            nuevo: $"{nueva.Etiqueta} Vigente desde {vigenciaUtc:yyyy-MM-dd HH:mm} UTC; próxima revisión {nueva.FechaProximaRevisionUtc:yyyy-MM-dd}");

        // Avisan el responsable del documento y el autor de la revisión (una sola vez si son la misma persona), sin
        // avisarle a quien ejecutó la acción. Va en esta misma transacción: si algo falla, no sale ningún correo.
        var valores = notificador.ValoresBase(nueva.Documento!, nueva.Etiqueta);
        valores["Fecha"] = notificador.Fecha(vigenciaUtc);
        valores["Enlace"] = valores["EnlaceVisor"];
        foreach (var destinatario in new[] { nueva.Documento!.ResponsableId, nueva.AutorId }.Distinct())
        {
            await notificador.EncolarAsync(db, EventosCorreo.Publicada, destinatario, valores, codigoDocumento,
                omitirIdentificador: actor.Identificador, ct: ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
