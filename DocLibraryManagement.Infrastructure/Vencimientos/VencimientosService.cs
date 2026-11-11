using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Correo;
using DocLibraryManagement.Infrastructure.Documentos;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocLibraryManagement.Infrastructure.Vencimientos;

internal sealed class VencimientosService(
    IDbContextFactory<AppDbContext> fabrica,
    IEncoladorNotificaciones notificador,
    TimeProvider reloj,
    ILogger<VencimientosService> log) : IVencimientosService
{
    private const string MensajeConcurrencia = "La solicitud cambió mientras la atendías. Recarga la página e inténtalo de nuevo.";

    // ---------- Proceso de fondo: avisos y escalamiento ----------

    public async Task<ResumenVencimientos> ProcesarAsync(CancellationToken ct = default)
    {
        var ahora = reloj.GetUtcNow().UtcDateTime;
        List<int> porRevisar;
        List<int> porEntrarEnVigor;

        await using (var consulta = await fabrica.CreateDbContextAsync(ct))
        {
            var limiteAviso = ahora.AddDays(ReglasVencimiento.DiasAvisoPrimero);
            porRevisar = await consulta.Revisiones.AsNoTracking()
                .Where(r => r.Estado == EstadoRevision.Vigente
                            && r.Documento!.Estado == EstadoDocumento.Activo
                            && r.FechaProximaRevisionUtc != null
                            && r.FechaProximaRevisionUtc <= limiteAviso)
                .OrderBy(r => r.FechaProximaRevisionUtc)
                .Select(r => r.Id)
                .ToListAsync(ct);

            var limiteVigencia = ahora.AddDays(ReglasVencimiento.DiasAvisoVigenciaProxima);
            porEntrarEnVigor = await consulta.Revisiones.AsNoTracking()
                .Where(r => r.Estado == EstadoRevision.Aprobado
                            && r.Documento!.Estado == EstadoDocumento.Activo
                            && r.FechaVigenciaUtc != null
                            && r.FechaVigenciaUtc > ahora
                            && r.FechaVigenciaUtc <= limiteVigencia)
                .Select(r => r.Id)
                .ToListAsync(ct);
        }

        var avisos = 0;
        foreach (var id in porRevisar)
        {
            avisos += await ConProteccionAsync(id, "vencimiento", i => AvisarVencimientoAsync(i, ahora, ct));
        }

        foreach (var id in porEntrarEnVigor)
        {
            avisos += await ConProteccionAsync(id, "vigencia próxima", i => AvisarVigenciaProximaAsync(i, ahora, ct));
        }

        return new ResumenVencimientos(avisos, porRevisar.Count + porEntrarEnVigor.Count);
    }

    /// <summary>Un documento con problemas no debe impedir que se avise de los demás; se reintenta en la siguiente pasada.</summary>
    private async Task<int> ConProteccionAsync(int revisionId, string que, Func<int, Task<int>> accion)
    {
        try
        {
            return await accion(revisionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Falló el aviso de {Que} de la revisión {RevisionId}", que, revisionId);
            return 0;
        }
    }

    private async Task<int> AvisarVencimientoAsync(int revisionId, DateTime ahora, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones
            .Include(x => x.Documento!).ThenInclude(d => d.Area!).ThenInclude(a => a.Dueno)
            .Include(x => x.Documento!).ThenInclude(d => d.Responsable)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct);

        // Otra instancia (o un retiro) pudo cambiarla entre la consulta y ahora.
        if (r is not { Estado: EstadoRevision.Vigente, FechaProximaRevisionUtc: { } proxima } || r.Documento!.Estado != EstadoDocumento.Activo)
        {
            return 0;
        }

        var estado = ReglasVencimiento.Clasificar(proxima, ahora);
        if (estado == EstadoVencimiento.AlDia)
        {
            return 0;
        }

        // La marca de la fecha en la clave hace que, al extender la fecha (revisado sin cambios o nueva revisión), el
        // ciclo siguiente vuelva a avisar; y que dentro del mismo ciclo cada aviso salga una sola vez.
        var marca = ReglasVencimiento.MarcaFecha(proxima);
        var doc = r.Documento;
        var valores = notificador.ValoresBase(doc, r.Etiqueta);
        valores["Fecha"] = notificador.Fecha(proxima);
        valores["Responsable"] = doc.Responsable?.NombreVisible ?? "(sin responsable)";

        if (estado == EstadoVencimiento.Vencido)
        {
            await notificador.EncolarAsync(db, EventosCorreo.DocumentoVencido, doc.ResponsableId, valores, doc.Codigo,
                clave: $"Vencido:{r.Id}:{marca}:{doc.ResponsableId}", ct: ct);

            foreach (var destino in await DestinatariosDeSeguimientoAsync(db, doc.Area!, excluirId: doc.ResponsableId, ct))
            {
                await notificador.EncolarAsync(db, EventosCorreo.EscalamientoVencido, destino, valores, doc.Codigo,
                    clave: $"Escalamiento:{r.Id}:{marca}:{destino}", ct: ct);
            }
        }
        else
        {
            var nivel = estado == EstadoVencimiento.PorVencer7 ? ReglasVencimiento.DiasAvisoUrgente : ReglasVencimiento.DiasAvisoPrimero;
            valores["Dias"] = ReglasVencimiento.DiasRestantes(proxima, ahora).ToString();
            await notificador.EncolarAsync(db, EventosCorreo.AlertaVencimiento, doc.ResponsableId, valores, doc.Codigo,
                clave: $"Venc{nivel}:{r.Id}:{marca}:{doc.ResponsableId}", ct: ct);
        }

        return await GuardarAvisosAsync(db, ct);
    }

    private async Task<int> AvisarVigenciaProximaAsync(int revisionId, DateTime ahora, CancellationToken ct)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.Include(x => x.Documento!).FirstOrDefaultAsync(x => x.Id == revisionId, ct);
        if (r is not { Estado: EstadoRevision.Aprobado, FechaVigenciaUtc: { } vigencia } || r.Documento!.Estado != EstadoDocumento.Activo || vigencia <= ahora)
        {
            return 0;
        }

        var valores = notificador.ValoresBase(r.Documento, r.Etiqueta);
        valores["Fecha"] = notificador.Fecha(vigencia);
        var marca = ReglasVencimiento.MarcaFecha(vigencia);
        foreach (var destino in new[] { r.AutorId, r.Documento.ResponsableId }.Distinct())
        {
            await notificador.EncolarAsync(db, EventosCorreo.VigenciaProxima, destino, valores, r.Documento.Codigo,
                clave: $"VigProx:{r.Id}:{marca}:{destino}", ct: ct);
        }

        return await GuardarAvisosAsync(db, ct);
    }

    /// <summary>Guarda y devuelve cuántos avisos nuevos había. Si otro proceso ya los encoló, no es un error.</summary>
    private static async Task<int> GuardarAvisosAsync(AppDbContext db, CancellationToken ct)
    {
        var nuevos = db.ChangeTracker.Entries<Notificacion>().Count(e => e.State == EntityState.Added);
        if (nuevos == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct);
            return nuevos;
        }
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            return 0; // la clave única hizo su trabajo: otra instancia se adelantó
        }
    }

    /// <summary>
    /// A quién le toca dar seguimiento: el dueño del área si existe, está activo y no es la persona a excluir; si no,
    /// todos los Administradores activos (menos esa persona).
    /// </summary>
    private static async Task<List<Guid>> DestinatariosDeSeguimientoAsync(AppDbContext db, Area area, Guid excluirId, CancellationToken ct)
    {
        if (area.Dueno is { Activo: true } dueno && dueno.Id != excluirId)
        {
            return [dueno.Id];
        }

        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Activo && u.Id != excluirId).ToListAsync(ct);
        return [.. usuarios.Where(u => u.TuvoRolEnUltimoAcceso(RolSistema.Administrador)).Select(u => u.Id)];
    }

    // ---------- Revisado sin cambios ----------

    public async Task<int> ProponerSinCambiosAsync(Actor actor, int revisionId, string comentario, CancellationToken ct = default)
    {
        var texto = Normalizar(comentario, "Escribe qué revisaste y por qué el documento no necesita cambios.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones
            .Include(x => x.Documento!).ThenInclude(d => d.Area!).ThenInclude(a => a.Dueno)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw new ReglaNegocioException("La revisión no existe.");
        var doc = r.Documento!;
        var usuario = await UsuarioDeAsync(db, actor, ct);

        if (!actor.EsAdministrador && !(usuario.Id == doc.ResponsableId && actor.PuedeEditarEnArea(doc.Area!.Codigo)))
        {
            throw new AccesoDenegadoException("Solo el responsable del documento (o un Administrador) puede declararlo revisado sin cambios.");
        }

        if (doc.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado.");
        }

        if (r.Estado != EstadoRevision.Vigente || r.FechaProximaRevisionUtc is not { } proxima)
        {
            throw new ReglaNegocioException($"Solo se puede declarar revisada sin cambios la revisión vigente. La {r.Etiqueta} está en estado {r.Estado}.");
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (!ReglasVencimiento.PuedeProponerSinCambios(proxima, ahora))
        {
            throw new ReglaNegocioException(
                $"Todavía no toca revisar este documento: su revisión periódica vence el {proxima:dd/MM/yyyy}. "
                + $"Podrás hacerlo cuando falten {ReglasVencimiento.DiasAvisoPrimero} días o menos.");
        }

        if (await db.SolicitudesSinCambios.AnyAsync(s => s.RevisionId == r.Id && s.Estado == EstadoSolicitudSinCambios.Pendiente, ct))
        {
            throw new ReglaNegocioException("Ya hay una solicitud pendiente de decisión para esta revisión.");
        }

        var solicitud = new SolicitudSinCambios
        {
            RevisionId = r.Id,
            SolicitanteId = usuario.Id,
            Comentario = texto,
            FechaSolicitudUtc = ahora,
            FechaProximaAnteriorUtc = proxima,
        };
        db.SolicitudesSinCambios.Add(solicitud);

        Bitacora.Registrar(db, reloj, actor, "Revision.SinCambios.Proponer", nameof(Documento), doc.Codigo,
            anterior: $"{r.Etiqueta} con próxima revisión el {proxima:yyyy-MM-dd}",
            nuevo: "Solicitud «revisado sin cambios» pendiente de la decisión del dueño del área", comentario: texto);

        var valores = notificador.ValoresBase(doc, r.Etiqueta);
        valores["Actor"] = usuario.NombreVisible;
        valores["Fecha"] = notificador.Fecha(proxima);
        valores["Motivo"] = texto;
        foreach (var destino in await DestinatariosDeSeguimientoAsync(db, doc.Area!, excluirId: usuario.Id, ct))
        {
            await notificador.EncolarAsync(db, EventosCorreo.SinCambiosSolicitada, destino, valores, doc.Codigo, ct: ct);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            throw new ReglaNegocioException("Otra persona acaba de enviar la misma solicitud. Recarga la página.");
        }

        return solicitud.Id;
    }

    public async Task DecidirSinCambiosAsync(Actor actor, int solicitudId, bool aprobar, string? comentario, CancellationToken ct = default)
    {
        var texto = (comentario ?? "").Trim();
        if (!aprobar && texto.Length == 0)
        {
            throw new ReglaNegocioException("Indica el motivo del rechazo.");
        }

        if (texto.Length > ReglasVencimiento.MaximoComentario)
        {
            throw new ReglaNegocioException($"El comentario no puede pasar de {ReglasVencimiento.MaximoComentario} caracteres.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var s = await CargarSolicitudAsync(db, solicitudId, ct);
        var r = s.Revision!;
        var doc = r.Documento!;
        var usuario = await UsuarioDeAsync(db, actor, ct);

        // Separación de funciones: quien propone nunca decide su propia solicitud (ni siquiera siendo dueño o Administrador).
        if (usuario.Id == s.SolicitanteId)
        {
            throw new AccesoDenegadoException("No puedes decidir una solicitud que tú mismo propusiste: la decide otra persona.");
        }

        var esDueno = doc.Area!.DuenoId == usuario.Id && actor.TieneRol(RolSistema.Aprobador);
        if (!actor.EsAdministrador && !esDueno)
        {
            throw new AccesoDenegadoException("Solo el dueño del área (o un Administrador) puede decidir esta solicitud.");
        }

        if (s.Estado != EstadoSolicitudSinCambios.Pendiente)
        {
            throw new ReglaNegocioException($"La solicitud ya fue resuelta ({s.Estado}).");
        }

        if (r.Estado != EstadoRevision.Vigente || doc.Estado != EstadoDocumento.Activo || r.FechaProximaRevisionUtc is not { } actual)
        {
            throw new ReglaNegocioException($"La {r.Etiqueta} ya no es la revisión vigente de un documento activo.");
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        s.DecididaPorId = usuario.Id;
        s.FechaDecisionUtc = ahora;
        s.ComentarioDecision = texto.Length == 0 ? null : texto;

        var valores = notificador.ValoresBase(doc, r.Etiqueta);
        valores["Actor"] = usuario.NombreVisible;
        valores["Motivo"] = texto.Length == 0 ? "(sin comentario)" : texto;

        var detalleBitacora = $"Propuesto por {s.Solicitante!.NombreVisible}: {s.Comentario}"
                              + (texto.Length == 0 ? "" : $" | Decisión: {texto}");

        if (aprobar)
        {
            var nueva = ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(actual, ahora, doc.TipoDocumento!.MesesRevision);
            r.FechaProximaRevisionUtc = nueva;
            s.Estado = EstadoSolicitudSinCambios.Aprobada;
            s.FechaProximaNuevaUtc = nueva;

            Bitacora.Registrar(db, reloj, actor, "Revision.SinCambios.Aprobar", nameof(Documento), doc.Codigo,
                anterior: $"{r.Etiqueta} con próxima revisión el {actual:yyyy-MM-dd}",
                nuevo: $"{r.Etiqueta} revisada sin cambios; próxima revisión el {nueva:yyyy-MM-dd}",
                comentario: Recortar(detalleBitacora));

            valores["Resultado"] = "aprobó";
            valores["Fecha"] = notificador.Fecha(nueva);
            valores["Detalle"] = $"La próxima revisión quedó para el {valores["Fecha"]}.";
        }
        else
        {
            s.Estado = EstadoSolicitudSinCambios.Rechazada;

            Bitacora.Registrar(db, reloj, actor, "Revision.SinCambios.Rechazar", nameof(Documento), doc.Codigo,
                anterior: "Solicitud «revisado sin cambios» pendiente", nuevo: "Rechazada; la fecha de próxima revisión no cambia",
                comentario: Recortar(detalleBitacora));

            valores["Resultado"] = "rechazó";
            valores["Fecha"] = notificador.Fecha(actual);
            valores["Detalle"] = "La fecha de próxima revisión no cambió: abre una revisión nueva para actualizar el documento.";
        }

        await notificador.EncolarAsync(db, EventosCorreo.SinCambiosResuelta, s.SolicitanteId, valores, doc.Codigo,
            omitirIdentificador: actor.Identificador, ct: ct);

        await GuardarAsync(db, ct);
    }

    public async Task CancelarSinCambiosAsync(Actor actor, int solicitudId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var s = await CargarSolicitudAsync(db, solicitudId, ct);
        var usuario = await UsuarioDeAsync(db, actor, ct);

        if (!actor.EsAdministrador && usuario.Id != s.SolicitanteId)
        {
            throw new AccesoDenegadoException("Solo quien propuso la solicitud (o un Administrador) puede retirarla.");
        }

        if (s.Estado != EstadoSolicitudSinCambios.Pendiente)
        {
            throw new ReglaNegocioException($"La solicitud ya fue resuelta ({s.Estado}).");
        }

        s.Estado = EstadoSolicitudSinCambios.Cancelada;
        s.DecididaPorId = usuario.Id;
        s.FechaDecisionUtc = reloj.GetUtcNow().UtcDateTime;
        s.ComentarioDecision = "Retirada.";

        Bitacora.Registrar(db, reloj, actor, "Revision.SinCambios.Cancelar", nameof(Documento), s.Revision!.Documento!.Codigo,
            anterior: "Solicitud «revisado sin cambios» pendiente", nuevo: "Cancelada", comentario: "Retirada por " + usuario.NombreVisible);

        await GuardarAsync(db, ct);
    }

    // ---------- Consulta ----------

    public async Task<IReadOnlyList<SolicitudSinCambiosDto>> HistorialAsync(Actor actor, int revisionId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.AsNoTracking()
            .Include(x => x.Documento!).ThenInclude(d => d.Area!).ThenInclude(a => a.Dueno)
            .Include(x => x.Documento!).ThenInclude(d => d.Responsable)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw new ReglaNegocioException("La revisión no existe.");

        if (!PuedeVer(actor, r))
        {
            throw new AccesoDenegadoException("No tienes permiso para ver estas solicitudes.");
        }

        var filas = await ConDetalle(db.SolicitudesSinCambios.AsNoTracking().Where(s => s.RevisionId == revisionId))
            .OrderByDescending(x => x.Id).ToListAsync(ct);
        return [.. filas.Select(ADto)];
    }

    /// <summary>Gestores del área (y Auditor/Administrador), el responsable del documento y el dueño del área.</summary>
    private static bool PuedeVer(Actor actor, RevisionDocumento r)
    {
        var area = r.Documento!.Area!;
        var esParte = actor.Identificador.Equals(r.Documento.Responsable?.Identificador, StringComparison.OrdinalIgnoreCase)
                      || actor.Identificador.Equals(area.Dueno?.Identificador, StringComparison.OrdinalIgnoreCase);
        return PermisosDocumento.EsGestor(actor, area.Codigo) || esParte;
    }

    public async Task<PanelRevisionPeriodicaDto?> PanelAsync(Actor actor, int revisionId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.AsNoTracking()
            .Include(x => x.Documento!).ThenInclude(d => d.Area!).ThenInclude(a => a.Dueno)
            .Include(x => x.Documento!).ThenInclude(d => d.Responsable)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw new ReglaNegocioException("La revisión no existe.");

        if (r.Estado != EstadoRevision.Vigente || !PuedeVer(actor, r))
        {
            return null;
        }

        var doc = r.Documento!;
        var area = doc.Area!;
        var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct);
        var ahora = reloj.GetUtcNow().UtcDateTime;

        var filas = await ConDetalle(db.SolicitudesSinCambios.AsNoTracking().Where(s => s.RevisionId == revisionId))
            .OrderByDescending(x => x.Id).ToListAsync(ct);
        var pendiente = filas.FirstOrDefault(s => s.Estado == EstadoSolicitudSinCambios.Pendiente);

        var esResponsable = usuario is not null && usuario.Id == doc.ResponsableId && actor.PuedeEditarEnArea(area.Codigo);
        var esDueno = usuario is not null && area.DuenoId == usuario.Id && actor.TieneRol(RolSistema.Aprobador);

        return new PanelRevisionPeriodicaDto(
            r.Id, r.Etiqueta, r.FechaProximaRevisionUtc,
            ReglasVencimiento.Clasificar(r.FechaProximaRevisionUtc, ahora),
            r.FechaProximaRevisionUtc is { } p ? ReglasVencimiento.DiasRestantes(p, ahora) : 0,
            area.Dueno?.NombreVisible,
            pendiente is null ? null : ADto(pendiente),
            PuedeProponer: doc.Estado == EstadoDocumento.Activo && pendiente is null
                           && (actor.EsAdministrador || esResponsable)
                           && ReglasVencimiento.PuedeProponerSinCambios(r.FechaProximaRevisionUtc, ahora),
            PuedeDecidir: pendiente is not null && usuario is not null && usuario.Id != pendiente.SolicitanteId
                          && (actor.EsAdministrador || esDueno),
            PuedeCancelar: pendiente is not null && usuario is not null && (actor.EsAdministrador || usuario.Id == pendiente.SolicitanteId),
            [.. filas.Select(ADto)]);
    }

    public async Task<IReadOnlyList<SolicitudSinCambiosDto>> PendientesDeDecisionAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var usuario = await UsuarioDeAsync(db, actor, ct);

        var pendientes = db.SolicitudesSinCambios.AsNoTracking()
            .Where(s => s.Estado == EstadoSolicitudSinCambios.Pendiente && s.SolicitanteId != usuario.Id);

        if (!actor.EsAdministrador)
        {
            if (!actor.TieneRol(RolSistema.Aprobador))
            {
                return [];
            }

            pendientes = pendientes.Where(s => s.Revision!.Documento!.Area!.DuenoId == usuario.Id);
        }

        var filas = await ConDetalle(pendientes).OrderBy(x => x.FechaSolicitudUtc).ToListAsync(ct);
        return [.. filas.Select(ADto)];
    }

    private static IQueryable<SolicitudSinCambios> ConDetalle(IQueryable<SolicitudSinCambios> consulta) =>
        consulta.Include(s => s.Solicitante).Include(s => s.DecididaPor)
            .Include(s => s.Revision!).ThenInclude(r => r.Documento!);

    // La etiqueta es una propiedad calculada del modelo (no se traduce a SQL): por eso se mapea en memoria.
    private static SolicitudSinCambiosDto ADto(SolicitudSinCambios s) => new(
        s.Id, s.RevisionId, s.Revision!.DocumentoId, s.Revision.Documento!.Codigo, s.Revision.Documento.Titulo, s.Revision.Etiqueta,
        s.Solicitante!.NombreVisible, s.Comentario, s.Estado, s.FechaSolicitudUtc, s.FechaProximaAnteriorUtc,
        s.FechaProximaNuevaUtc, s.DecididaPor?.NombreVisible, s.ComentarioDecision, s.FechaDecisionUtc);

    // ---------- Apoyo ----------

    private static async Task<SolicitudSinCambios> CargarSolicitudAsync(AppDbContext db, int id, CancellationToken ct) =>
        await db.SolicitudesSinCambios
            .Include(s => s.Solicitante)
            .Include(s => s.Revision!).ThenInclude(r => r.Documento!).ThenInclude(d => d.Area!)
            .Include(s => s.Revision!).ThenInclude(r => r.Documento!).ThenInclude(d => d.TipoDocumento)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new ReglaNegocioException("La solicitud no existe.");

    private static async Task<Usuario> UsuarioDeAsync(AppDbContext db, Actor actor, CancellationToken ct) =>
        await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct)
        ?? throw new ReglaNegocioException("Tu usuario aún no está registrado. Cierra sesión y vuelve a entrar.");

    private static string Normalizar(string? texto, string mensajeObligatorio)
    {
        var limpio = (texto ?? "").Trim();
        if (limpio.Length == 0)
        {
            throw new ReglaNegocioException(mensajeObligatorio);
        }

        if (limpio.Length > ReglasVencimiento.MaximoComentario)
        {
            throw new ReglaNegocioException($"El comentario no puede pasar de {ReglasVencimiento.MaximoComentario} caracteres.");
        }

        return limpio;
    }

    /// <summary>El comentario de la bitácora admite 2,000 caracteres; propuesta y decisión juntas podrían pasarse.</summary>
    private static string Recortar(string texto) => texto.Length <= 2000 ? texto : texto[..1999] + "…";

    private static async Task GuardarAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ReglaNegocioException(MensajeConcurrencia);
        }
    }
}

/// <summary>Cierre de las solicitudes «sin cambios» que dejan de tener sentido (la revisión ya no es la vigente).</summary>
internal static class SolicitudesSinCambiosCierre
{
    public static async Task CancelarPendientesAsync(
        AppDbContext db, TimeProvider reloj, Actor actor, IReadOnlyCollection<int> revisionIds, string codigoDocumento, string motivo, CancellationToken ct)
    {
        if (revisionIds.Count == 0)
        {
            return;
        }

        var pendientes = await db.SolicitudesSinCambios
            .Where(s => revisionIds.Contains(s.RevisionId) && s.Estado == EstadoSolicitudSinCambios.Pendiente)
            .ToListAsync(ct);

        foreach (var s in pendientes)
        {
            s.Estado = EstadoSolicitudSinCambios.Cancelada;
            s.FechaDecisionUtc = reloj.GetUtcNow().UtcDateTime;
            s.ComentarioDecision = motivo;
            Bitacora.Registrar(db, reloj, actor, "Revision.SinCambios.Cancelar", nameof(Documento), codigoDocumento,
                anterior: "Solicitud «revisado sin cambios» pendiente", nuevo: "Cancelada", comentario: motivo);
        }
    }
}
