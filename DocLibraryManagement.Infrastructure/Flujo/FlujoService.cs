using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Documentos;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Flujo;

internal sealed class FlujoService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : IFlujoService
{
    private const string MensajeConcurrencia =
        "El flujo cambió mientras lo consultabas (alguien más decidió o lo canceló). Recarga la página.";

    private const string MensajeAutorNoPuede =
        "El autor de la revisión no puede revisarla ni aprobarla (separación de funciones).";

    // ---------- Plantillas ----------

    public async Task<IReadOnlyList<PasoPlantillaDto>> ObtenerPlantillaAsync(int tipoDocumentoId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await db.PasosPlantilla.AsNoTracking()
            .Where(p => p.TipoDocumentoId == tipoDocumentoId)
            .OrderBy(p => p.Orden)
            .Select(p => new PasoPlantillaDto(p.Orden, p.Clase, p.Nombre))
            .ToListAsync(ct);
    }

    public async Task GuardarPlantillaAsync(Actor actor, int tipoDocumentoId, IReadOnlyList<PasoPlantillaDto> pasos, CancellationToken ct = default)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede modificar los flujos.");
        }

        var nuevos = ReglasFlujo.NormalizarPlantilla(pasos);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var tipo = await db.TiposDocumento.FirstOrDefaultAsync(t => t.Id == tipoDocumentoId, ct)
            ?? throw new ReglaNegocioException("El tipo de documento no existe.");

        var existentes = await db.PasosPlantilla.Where(p => p.TipoDocumentoId == tipoDocumentoId).OrderBy(p => p.Orden).ToListAsync(ct);
        var anterior = Describir(existentes.Select(p => new PasoPlantillaDto(p.Orden, p.Clase, p.Nombre)));

        // Se reemplaza en bloque: el índice único (tipo, orden) obliga a borrar antes de insertar.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PasosPlantilla.RemoveRange(existentes);
        await db.SaveChangesAsync(ct);

        db.PasosPlantilla.AddRange(nuevos.Select(p => new PasoPlantilla
        {
            TipoDocumentoId = tipoDocumentoId,
            Orden = p.Orden,
            Clase = p.Clase,
            Nombre = p.Nombre,
        }));
        Bitacora.Registrar(db, reloj, actor, "Flujo.GuardarPlantilla", nameof(TipoDocumento), tipo.Codigo, anterior, Describir(nuevos));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Describir(IEnumerable<PasoPlantillaDto> pasos) =>
        string.Join(" → ", pasos.Select(p => $"{p.Orden}.{(p.Clase == TipoPaso.Revision ? "Rev" : "Apr")}:{p.Nombre}"));

    // ---------- Candidatos ----------

    public async Task<IReadOnlyList<CandidatoDto>> CandidatosAsync(TipoPaso clase, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await CandidatosInternoAsync(db, clase, ct);
    }

    private static async Task<List<CandidatoDto>> CandidatosInternoAsync(AppDbContext db, TipoPaso clase, CancellationToken ct)
    {
        var rol = clase.RolRequerido();

        // La columna es una lista separada por comas: se acota con LIKE y se confirma exacto en memoria.
        var usuarios = await db.Usuarios.AsNoTracking()
            .Where(u => u.Activo && u.RolesUltimoAcceso.Contains(rol.ToString()))
            .OrderBy(u => u.NombreVisible)
            .ToListAsync(ct);

        return usuarios.Where(u => u.TuvoRolEnUltimoAcceso(rol))
            .Select(u => new CandidatoDto(u.Id, u.NombreVisible, u.Identificador))
            .ToList();
    }

    // ---------- Iniciar ----------

    public async Task IniciarAsync(Actor actor, int revisionId, IReadOnlyList<AsignacionPaso> asignaciones, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var iniciador = await UsuarioDeAsync(db, actor, ct);
        var r = await CargarRevisionAsync(db, revisionId, ct);

        if (!actor.PuedeEditarEnArea(r.Documento!.Area!.Codigo))
        {
            throw new AccesoDenegadoException($"No tienes permiso para enviar documentos del área {r.Documento.Area.Codigo} a revisión.");
        }

        if (r.Documento.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado.");
        }

        if (r.Estado != EstadoRevision.Borrador)
        {
            throw new ReglaNegocioException("Solo se puede enviar a revisión una revisión en borrador.");
        }

        if (r.RutaArchivoPdf is null)
        {
            throw new ReglaNegocioException("Sube el PDF antes de enviar la revisión a revisión.");
        }

        var plantilla = await db.PasosPlantilla.AsNoTracking()
            .Where(p => p.TipoDocumentoId == r.Documento.TipoDocumentoId)
            .OrderBy(p => p.Orden)
            .ToListAsync(ct);
        if (plantilla.Count == 0)
        {
            throw new ReglaNegocioException("Este tipo de documento no tiene un flujo configurado. Pide a un Administrador que lo configure.");
        }

        var porOrden = asignaciones.GroupBy(a => a.Orden).ToDictionary(g => g.Key, g => g.ToList());
        if (porOrden.Any(kv => kv.Value.Count > 1) || plantilla.Any(p => !porOrden.ContainsKey(p.Orden))
            || porOrden.Keys.Any(o => plantilla.All(p => p.Orden != o)))
        {
            throw new ReglaNegocioException("Asigna exactamente una persona a cada etapa del flujo.");
        }

        var ids = asignaciones.Select(a => a.UsuarioId).Distinct().ToList();
        var usuarios = await db.Usuarios.Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        var pasos = new List<PasoInstancia>();
        foreach (var p in plantilla)
        {
            var asignado = ValidarAsignado(usuarios.GetValueOrDefault(porOrden[p.Orden][0].UsuarioId), p.Clase, r.AutorId, p.Nombre);
            pasos.Add(new PasoInstancia { Orden = p.Orden, Clase = p.Clase, Nombre = p.Nombre, AsignadoAId = asignado.Id });
        }

        var numero = (await db.InstanciasFlujo.Where(i => i.RevisionId == revisionId).MaxAsync(i => (int?)i.Numero, ct) ?? 0) + 1;
        var ahora = reloj.GetUtcNow().UtcDateTime;
        db.InstanciasFlujo.Add(new InstanciaFlujo
        {
            RevisionId = revisionId,
            Numero = numero,
            Estado = EstadoFlujo.EnCurso,
            FechaInicioUtc = ahora,
            IniciadoPorId = iniciador.Id,
            Pasos = pasos,
        });

        r.Estado = pasos[0].Clase.EstadoDeRevision();

        var resumen = string.Join("; ", pasos.Select(p => $"{p.Orden}.{p.Nombre}→{usuarios[p.AsignadoAId].NombreVisible}"));
        Bitacora.Registrar(db, reloj, actor, "Flujo.Iniciar", nameof(Documento), r.Documento.Codigo,
            anterior: $"{r.Etiqueta} Borrador", nuevo: $"{r.Etiqueta} {r.Estado}; flujo #{numero}: {resumen}");

        await GuardarAsync(db, ct);
    }

    // ---------- Consulta de flujos y tareas ----------

    public async Task<IReadOnlyList<InstanciaFlujoDto>> HistorialAsync(Actor actor, int revisionId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await db.Revisiones.AsNoTracking().Include(x => x.Documento!).ThenInclude(d => d.Area)
            .FirstOrDefaultAsync(x => x.Id == revisionId, ct)
            ?? throw new ReglaNegocioException("La revisión no existe.");

        if (!await PermisosDocumento.PuedeVerDocumentoAsync(db, actor, r.Documento!.Area!.Codigo, r.DocumentoId, ct))
        {
            throw new AccesoDenegadoException("No tienes permiso para ver el flujo de esta revisión.");
        }

        var instancias = await db.InstanciasFlujo.AsNoTracking()
            .Where(i => i.RevisionId == revisionId)
            .OrderByDescending(i => i.Numero)
            .Select(i => new
            {
                i.Id, i.Numero, i.Estado, i.FechaInicioUtc, i.FechaFinUtc,
                IniciadoPor = i.IniciadoPor!.NombreVisible,
                Pasos = i.Pasos.OrderBy(p => p.Orden).Select(p => new PasoInstanciaDto(
                    p.Id, p.Orden, p.Clase, p.Nombre, p.AsignadoAId, p.AsignadoA!.NombreVisible, p.Estado,
                    p.DecididoPor != null ? p.DecididoPor.NombreVisible : null, p.FechaDecisionUtc, p.Comentario)).ToList(),
            })
            .ToListAsync(ct);

        return instancias.Select(i => new InstanciaFlujoDto(i.Id, i.Numero, i.Estado, i.FechaInicioUtc, i.FechaFinUtc, i.IniciadoPor, i.Pasos)).ToList();
    }

    public async Task<IReadOnlyList<TareaPendienteDto>> MisTareasAsync(Actor actor, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        // Solo la etapa vigente de cada flujo en curso: las siguientes aún no le tocan a nadie.
        var filas = await db.PasosInstancia.AsNoTracking()
            .Where(p => p.Estado == EstadoPaso.Pendiente
                        && p.Instancia!.Estado == EstadoFlujo.EnCurso
                        && p.AsignadoA!.Identificador == actor.Identificador
                        && p.Orden == p.Instancia.Pasos.Where(x => x.Estado == EstadoPaso.Pendiente).Min(x => x.Orden))
            .OrderBy(p => p.Instancia!.FechaInicioUtc)
            .Select(p => new
            {
                p.Id, p.Instancia!.RevisionId, p.Instancia.Revision!.DocumentoId,
                p.Instancia.Revision.Documento!.Codigo, p.Instancia.Revision.Documento.Titulo,
                p.Instancia.Revision.Mayor, p.Instancia.Revision.Menor,
                p.Clase, p.Nombre, p.Instancia.FechaInicioUtc,
            })
            .ToListAsync(ct);

        return filas.Select(f => new TareaPendienteDto(
            f.Id, f.RevisionId, f.DocumentoId, f.Codigo, f.Titulo,
            RevisionDocumento.FormatearEtiqueta(f.Mayor, f.Menor), f.Clase, f.Nombre, f.FechaInicioUtc)).ToList();
    }

    // ---------- Decidir ----------

    public async Task DecidirAsync(Actor actor, int pasoId, bool aprobar, string? comentario, CancellationToken ct = default)
    {
        var texto = ReglasFlujo.NormalizarComentario(comentario, obligatorio: !aprobar,
            "Al rechazar debes explicar el motivo para que el autor sepa qué corregir.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var decisor = await UsuarioDeAsync(db, actor, ct);

        var paso = await db.PasosInstancia
            .Include(p => p.AsignadoA)
            .Include(p => p.Instancia!).ThenInclude(i => i.Pasos)
            .Include(p => p.Instancia!).ThenInclude(i => i.Revision!).ThenInclude(r => r.Documento)
            .FirstOrDefaultAsync(p => p.Id == pasoId, ct)
            ?? throw new ReglaNegocioException("La etapa no existe.");

        var instancia = paso.Instancia!;
        var revision = instancia.Revision!;

        if (instancia.Estado != EstadoFlujo.EnCurso || paso.Estado != EstadoPaso.Pendiente)
        {
            throw new ReglaNegocioException("Esta etapa ya no está pendiente.");
        }

        var vigente = instancia.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente).MinBy(p => p.Orden)!;
        if (vigente.Id != paso.Id)
        {
            throw new ReglaNegocioException("Todavía no es el turno de esta etapa.");
        }

        if (paso.AsignadoA!.Identificador != actor.Identificador)
        {
            throw new AccesoDenegadoException("Esta etapa está asignada a otra persona.");
        }

        // Se revalida con los roles VIVOS (de los grupos de AD), no con la copia guardada al iniciar sesión.
        if (!actor.TieneRol(paso.Clase.RolRequerido()))
        {
            throw new AccesoDenegadoException($"Ya no tienes el rol {paso.Clase.RolRequerido()} necesario para decidir esta etapa.");
        }

        if (revision.AutorId == decisor.Id)
        {
            throw new AccesoDenegadoException(MensajeAutorNoPuede);
        }

        var ahora = reloj.GetUtcNow().UtcDateTime;
        paso.DecididoPorId = decisor.Id;
        paso.FechaDecisionUtc = ahora;
        paso.Comentario = texto.Length == 0 ? null : texto;

        string resultado;
        if (aprobar)
        {
            paso.Estado = EstadoPaso.Aprobado;
            var siguiente = instancia.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente && p.Id != paso.Id).MinBy(p => p.Orden);
            if (siguiente is not null)
            {
                revision.Estado = siguiente.Clase.EstadoDeRevision();
                resultado = $"Aprobada; sigue «{siguiente.Nombre}»";
            }
            else
            {
                instancia.Estado = EstadoFlujo.Aprobado;
                instancia.FechaFinUtc = ahora;
                revision.Estado = EstadoRevision.Aprobado;
                revision.FechaAprobacionUtc = ahora;
                resultado = "Aprobada; flujo completo, revisión Aprobada";
            }
        }
        else
        {
            paso.Estado = EstadoPaso.Rechazado;
            foreach (var restante in instancia.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente && p.Id != paso.Id))
            {
                restante.Estado = EstadoPaso.Omitido;
            }

            instancia.Estado = EstadoFlujo.Rechazado;
            instancia.FechaFinUtc = ahora;
            revision.Estado = EstadoRevision.Borrador;
            resultado = "Rechazada; la revisión vuelve a Borrador";
        }

        Bitacora.Registrar(db, reloj, actor, aprobar ? "Flujo.Aprobar" : "Flujo.Rechazar", nameof(Documento), revision.Documento!.Codigo,
            nuevo: $"{revision.Etiqueta} «{paso.Nombre}»: {resultado}", comentario: paso.Comentario);

        await GuardarAsync(db, ct);
    }

    // ---------- Reasignar y cancelar ----------

    public async Task ReasignarAsync(Actor actor, int pasoId, Guid nuevoUsuarioId, string motivo, CancellationToken ct = default)
    {
        var texto = ReglasFlujo.NormalizarComentario(motivo, obligatorio: true, "Indica el motivo de la reasignación.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var paso = await db.PasosInstancia
            .Include(p => p.AsignadoA)
            .Include(p => p.Instancia!).ThenInclude(i => i.Revision!).ThenInclude(r => r.Documento!).ThenInclude(d => d.Area)
            .FirstOrDefaultAsync(p => p.Id == pasoId, ct)
            ?? throw new ReglaNegocioException("La etapa no existe.");

        var revision = paso.Instancia!.Revision!;
        if (!actor.PuedeEditarEnArea(revision.Documento!.Area!.Codigo))
        {
            throw new AccesoDenegadoException("Solo quien edita en el área del documento puede reasignar etapas.");
        }

        if (paso.Instancia.Estado != EstadoFlujo.EnCurso || paso.Estado != EstadoPaso.Pendiente)
        {
            throw new ReglaNegocioException("Solo se puede reasignar una etapa pendiente de un flujo en curso.");
        }

        var nuevo = ValidarAsignado(await db.Usuarios.FirstOrDefaultAsync(u => u.Id == nuevoUsuarioId, ct), paso.Clase, revision.AutorId, paso.Nombre);
        var anterior = paso.AsignadoA!.NombreVisible;
        paso.AsignadoAId = nuevo.Id;

        Bitacora.Registrar(db, reloj, actor, "Flujo.Reasignar", nameof(Documento), revision.Documento.Codigo,
            anterior: $"{revision.Etiqueta} «{paso.Nombre}»: {anterior}", nuevo: nuevo.NombreVisible, comentario: texto);

        await GuardarAsync(db, ct);
    }

    public async Task CancelarFlujoAsync(Actor actor, int revisionId, string motivo, CancellationToken ct = default)
    {
        var texto = ReglasFlujo.NormalizarComentario(motivo, obligatorio: true, "Indica el motivo de la cancelación.");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await CargarRevisionAsync(db, revisionId, ct);

        if (!actor.PuedeEditarEnArea(r.Documento!.Area!.Codigo))
        {
            throw new AccesoDenegadoException("Solo quien edita en el área del documento puede cancelar el flujo.");
        }

        var instancia = await db.InstanciasFlujo.Include(i => i.Pasos)
            .FirstOrDefaultAsync(i => i.RevisionId == revisionId && i.Estado == EstadoFlujo.EnCurso, ct)
            ?? throw new ReglaNegocioException("La revisión no tiene un flujo en curso.");

        foreach (var p in instancia.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente))
        {
            p.Estado = EstadoPaso.Omitido;
        }

        instancia.Estado = EstadoFlujo.Cancelado;
        instancia.FechaFinUtc = reloj.GetUtcNow().UtcDateTime;
        var estadoAnterior = r.Estado;
        r.Estado = EstadoRevision.Borrador;

        Bitacora.Registrar(db, reloj, actor, "Flujo.Cancelar", nameof(Documento), r.Documento.Codigo,
            anterior: $"{r.Etiqueta} {estadoAnterior}", nuevo: $"{r.Etiqueta} Borrador", comentario: texto);

        await GuardarAsync(db, ct);
    }

    // ---------- Apoyo ----------

    private static async Task<Usuario> UsuarioDeAsync(AppDbContext db, Actor actor, CancellationToken ct) =>
        await db.Usuarios.FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct)
        ?? throw new ReglaNegocioException("Tu usuario aún no está registrado. Cierra sesión y vuelve a entrar.");

    private static async Task<RevisionDocumento> CargarRevisionAsync(AppDbContext db, int id, CancellationToken ct) =>
        await db.Revisiones.Include(r => r.Documento!).ThenInclude(d => d.Area)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
        ?? throw new ReglaNegocioException("La revisión no existe.");

    /// <summary>Una persona asignada debe existir, estar activa, tener el rol de la etapa y no ser el autor.</summary>
    private static Usuario ValidarAsignado(Usuario? usuario, TipoPaso clase, Guid autorId, string nombrePaso)
    {
        if (usuario is null || !usuario.Activo)
        {
            throw new ReglaNegocioException($"La persona asignada a «{nombrePaso}» no existe o está inactiva.");
        }

        if (usuario.Id == autorId)
        {
            throw new ReglaNegocioException(MensajeAutorNoPuede);
        }

        if (!usuario.TuvoRolEnUltimoAcceso(clase.RolRequerido()))
        {
            throw new ReglaNegocioException(
                $"{usuario.NombreVisible} no tiene el rol {clase.RolRequerido()} necesario para «{nombrePaso}».");
        }

        return usuario;
    }

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
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            // Dos envíos simultáneos de la misma revisión: el índice único de «flujo en curso» deja pasar uno.
            throw new ReglaNegocioException("La revisión ya fue enviada a revisión. Recarga la página.");
        }
    }
}
