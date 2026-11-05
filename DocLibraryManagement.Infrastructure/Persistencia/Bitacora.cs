using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;

namespace DocLibraryManagement.Infrastructure.Persistencia;

internal static class Bitacora
{
    /// <summary>
    /// Agrega el evento al contexto. Debe guardarse en la misma transacción que el cambio auditado
    /// para que no pueda existir un cambio sin su registro, ni al revés.
    /// </summary>
    public static void Registrar(
        AppDbContext db,
        TimeProvider reloj,
        Actor actor,
        string accion,
        string entidad,
        string? entidadId,
        string? anterior = null,
        string? nuevo = null,
        string? comentario = null)
    {
        db.Auditoria.Add(new EventoAuditoria
        {
            FechaUtc = reloj.GetUtcNow().UtcDateTime,
            Usuario = actor.Identificador,
            Accion = accion,
            Entidad = entidad,
            EntidadId = entidadId,
            ValorAnterior = anterior,
            ValorNuevo = nuevo,
            Comentario = comentario,
            DireccionIp = actor.DireccionIp,
        });
    }

    /// <summary>Violación de índice único (2601) o de restricción única (2627) de SQL Server.</summary>
    public static bool EsViolacionUnica(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
