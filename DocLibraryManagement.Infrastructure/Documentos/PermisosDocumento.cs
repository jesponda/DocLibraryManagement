using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Documentos;

/// <summary>
/// Reglas de quién puede ver qué, en un solo lugar para que documentos, revisiones, archivos y flujos
/// no tengan criterios distintos.
/// </summary>
internal static class PermisosDocumento
{
    /// <summary>Administrador, Auditor y Autores del área: ven todo lo del documento, incluidos borradores.</summary>
    public static bool EsGestor(Actor actor, string codigoArea) =>
        actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor) || actor.PuedeEditarEnArea(codigoArea);

    /// <summary>Tiene (o tuvo) una etapa asignada en alguna revisión del documento.</summary>
    public static Task<bool> TieneEtapaAsignadaAsync(AppDbContext db, Actor actor, int documentoId, CancellationToken ct) =>
        db.PasosInstancia.AnyAsync(
            p => p.AsignadoA!.Identificador == actor.Identificador
                 && p.Instancia!.Revision!.DocumentoId == documentoId, ct);

    /// <summary>
    /// Es el dueño designado del área Y conserva el rol de Aprobador (los roles salen de AD, así que si se lo quitan
    /// pierde el acceso aunque el área lo siga nombrando). Ve los documentos de su área, solo lectura: no edita.
    /// </summary>
    public static async Task<bool> EsDuenoDelAreaAsync(AppDbContext db, Actor actor, string codigoArea, CancellationToken ct) =>
        actor.TieneRol(RolSistema.Aprobador)
        && await db.Areas.AnyAsync(a => a.Codigo == codigoArea && a.Dueno != null && a.Dueno.Identificador == actor.Identificador, ct);

    public static async Task<bool> PuedeVerDocumentoAsync(
        AppDbContext db, Actor actor, string codigoArea, int documentoId, CancellationToken ct) =>
        EsGestor(actor, codigoArea)
        || await EsDuenoDelAreaAsync(db, actor, codigoArea, ct)
        || await TieneEtapaAsignadaAsync(db, actor, documentoId, ct);

    /// <summary>Lo vigente es de lectura abierta; todo lo demás sigue la regla del documento.</summary>
    public static async Task<bool> PuedeVerRevisionAsync(
        AppDbContext db, Actor actor, string codigoArea, int documentoId, EstadoRevision estado, CancellationToken ct) =>
        estado == EstadoRevision.Vigente || await PuedeVerDocumentoAsync(db, actor, codigoArea, documentoId, ct);
}
