using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Catalogo;
using DocLibraryManagement.Infrastructure.Correo;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocLibraryManagement.Infrastructure.Documentos;

internal sealed class DocumentoService(
    IDbContextFactory<AppDbContext> fabrica,
    IEncoladorNotificaciones notificador,
    IOptions<DocumentosOptions> opciones,
    TimeProvider reloj) : IDocumentoService
{
    private const int MaximoTitulo = 300;
    private const int MaximoMotivo = 1000;

    /// <summary>
    /// Intentos ante colisión de consecutivo. Dos autores que crean a la vez en el mismo (tipo, área)
    /// calculan el mismo número: el índice único deja pasar a uno y el otro reintenta con el siguiente.
    /// </summary>
    private const int MaximoIntentos = 15;

    public async Task<DocumentoDetalleDto> CrearAsync(Actor actor, NuevoDocumento nuevo, CancellationToken ct = default)
    {
        var titulo = (nuevo.Titulo ?? "").Trim();
        if (titulo.Length == 0)
        {
            throw new ReglaNegocioException("El título del documento es obligatorio.");
        }

        if (titulo.Length > MaximoTitulo)
        {
            throw new ReglaNegocioException($"El título no puede pasar de {MaximoTitulo} caracteres.");
        }

        for (var intento = 1; ; intento++)
        {
            await using var db = await fabrica.CreateDbContextAsync(ct);

            var area = await db.Areas.AsNoTracking().FirstOrDefaultAsync(a => a.Id == nuevo.AreaId, ct)
                ?? throw new ReglaNegocioException("El área no existe.");

            if (!actor.PuedeEditarEnArea(area.Codigo))
            {
                throw new AccesoDenegadoException($"No tienes permiso para crear documentos en el área {area.Codigo}.");
            }

            if (!area.Activa)
            {
                throw new ReglaNegocioException($"El área {area.Codigo} está desactivada y no admite documentos nuevos.");
            }

            var sub = await ResolverUnidadAsync(db, area, nuevo.SubAreaId, ct);

            var disponibles = await CatalogoService.TiposDisponiblesAsync(db, area.Id, sub.Id, ct);
            var tipo = disponibles.FirstOrDefault(t => t.Id == nuevo.TipoDocumentoId)
                ?? throw new ReglaNegocioException(
                    "Ese tipo de documento no está habilitado para el área o subárea elegida, o está desactivado.");

            var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct)
                ?? throw new ReglaNegocioException("Tu usuario aún no está registrado. Cierra sesión y vuelve a entrar.");

            // El consecutivo es por tipo, área y unidad (estándar de código de Calidad) y nunca se reutiliza.
            var consecutivo = (await db.Documentos
                .Where(d => d.TipoDocumentoId == tipo.Id && d.AreaId == area.Id && d.SubAreaId == sub.Id)
                .MaxAsync(d => (int?)d.Consecutivo, ct) ?? 0) + 1;

            var ahora = reloj.GetUtcNow().UtcDateTime;
            var documento = new Documento
            {
                Codigo = Documento.ConstruirCodigo(Sitio(), tipo.Codigo, area.Codigo, sub.Codigo!, consecutivo),
                Titulo = titulo,
                AreaId = area.Id,
                SubAreaId = sub.Id,
                TipoDocumentoId = tipo.Id,
                Consecutivo = consecutivo,
                ResponsableId = usuario.Id,
                FechaCreacionUtc = ahora,
                Revisiones =
                [
                    new RevisionDocumento
                    {
                        Mayor = 0,
                        Menor = 0,
                        Estado = EstadoRevision.Borrador,
                        MotivoCambio = "Emisión inicial",
                        AutorId = usuario.Id,
                        FechaCreacionUtc = ahora,
                    },
                ],
            };

            db.Documentos.Add(documento);
            Bitacora.Registrar(db, reloj, actor, "Documento.Crear", nameof(Documento), documento.Codigo,
                nuevo: $"Titulo={titulo}; Area={area.Codigo}; Unidad={sub.Codigo}; Tipo={tipo.Codigo}; Rev.00 Borrador");

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex) && intento < MaximoIntentos)
            {
                // Otro autor tomó ese consecutivo primero. Pequeña espera aleatoria para no chocar de nuevo.
                await Task.Delay(Random.Shared.Next(5, 40), ct);
                continue;
            }
            catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
            {
                throw new ReglaNegocioException(
                    "No se pudo asignar un código al documento por exceso de solicitudes simultáneas. Inténtalo de nuevo.");
            }

            return (await ObtenerInternoAsync(db, documento.Id, ct))!;
        }
    }

    /// <summary>Código del sitio normalizado (primer segmento del código); vacío = sin prefijo. Una configuración inválida se dice clara.</summary>
    private string? Sitio()
    {
        var configurado = opciones.Value.CodigoSitio;
        if (string.IsNullOrWhiteSpace(configurado))
        {
            return null;
        }

        try
        {
            return ReglasCatalogo.NormalizarCodigo(configurado, "el sitio", ReglasCatalogo.LongitudMinimaCodigoTipo);
        }
        catch (ReglaNegocioException)
        {
            throw new InvalidOperationException(
                $"La opción 'Documentos:CodigoSitio' («{configurado}») no es válida: use de 1 a {ReglasCatalogo.LongitudMaximaCodigo} letras o números, o déjela vacía.");
        }
    }

    /// <summary>
    /// La unidad del documento (su subárea). Es obligatoria porque forma parte del código. Si el área tiene una sola unidad
    /// activa se usa esa; si tiene varias hay que elegir; si no tiene, hay que crearla antes.
    /// </summary>
    private static async Task<SubArea> ResolverUnidadAsync(AppDbContext db, Area area, int? subAreaId, CancellationToken ct)
    {
        if (subAreaId is { } id)
        {
            var elegida = await db.SubAreas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            if (elegida is null || elegida.AreaId != area.Id)
            {
                throw new ReglaNegocioException("La subárea no pertenece al área indicada.");
            }

            if (!elegida.Activa)
            {
                throw new ReglaNegocioException("La subárea está desactivada y no admite documentos nuevos.");
            }

            return elegida.Codigo is null
                ? throw new ReglaNegocioException(
                    $"La subárea «{elegida.Nombre}» no tiene abreviatura, así que no puede formar parte del código. Un Administrador debe asignársela.")
                : elegida;
        }

        var unidades = await db.SubAreas.AsNoTracking()
            .Where(s => s.AreaId == area.Id && s.Activa && s.Codigo != null)
            .OrderBy(s => s.Nombre)
            .ToListAsync(ct);

        // Sin subárea elegida: un documento de toda el área va a su unidad «General» (GEN), si la tiene.
        var general = unidades.FirstOrDefault(s => string.Equals(s.Codigo, ReglasCatalogo.UnidadGeneral, StringComparison.OrdinalIgnoreCase));

        return unidades.Count switch
        {
            1 => unidades[0],
            0 => throw new ReglaNegocioException(
                $"El área {area.Codigo} no tiene subáreas (unidades) activas con abreviatura: el código del documento las necesita. Pide a un Administrador que las cree."),
            _ when general is not null => general,
            _ => throw new ReglaNegocioException(
                "Elige la subárea (unidad) del documento: forma parte de su código y no se puede cambiar después. " +
                $"Para documentos de toda el área, un Administrador puede crear la unidad «{ReglasCatalogo.NombreUnidadGeneral}» ({ReglasCatalogo.UnidadGeneral})."),
        };
    }

    public async Task<IReadOnlyList<DocumentoResumenDto>> ListarGestionablesAsync(Actor actor, CancellationToken ct = default)
    {
        var veTodo = actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor);
        if (!veTodo && !actor.TieneRol(RolSistema.Autor))
        {
            return [];
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);

        var consulta = db.Documentos.AsNoTracking();
        if (!veTodo)
        {
            var codigos = actor.Areas.Select(a => a.ToUpperInvariant()).ToList();
            consulta = consulta.Where(d => codigos.Contains(d.Area!.Codigo));
        }

        var filas = await consulta
            .OrderBy(d => d.Codigo)
            .Select(d => new
            {
                d.Id,
                d.Codigo,
                d.Titulo,
                Area = d.Area!.Codigo + " – " + d.Area.Nombre,
                SubArea = d.SubArea != null ? d.SubArea.Nombre : null,
                Tipo = d.TipoDocumento!.Nombre,
                d.Estado,
                Ultima = d.Revisiones
                    .OrderByDescending(r => r.Mayor).ThenByDescending(r => r.Menor)
                    .Select(r => new { r.Mayor, r.Menor, r.Estado })
                    .First(),
            })
            .ToListAsync(ct);

        return filas.Select(f => new DocumentoResumenDto(
            f.Id, f.Codigo, f.Titulo, f.Area, f.SubArea, f.Tipo, f.Estado,
            RevisionDocumento.FormatearEtiqueta(f.Ultima.Mayor, f.Ultima.Menor), f.Ultima.Estado)).ToList();
    }

    public async Task<DocumentoDetalleDto?> ObtenerAsync(Actor actor, int documentoId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var detalle = await ObtenerInternoAsync(db, documentoId, ct);
        if (detalle is null)
        {
            return null;
        }

        // Gestores (Administrador, Auditor, Autores del área) y quienes tengan una etapa asignada en él.
        return await PermisosDocumento.PuedeVerDocumentoAsync(db, actor, detalle.CodigoArea, detalle.Id, ct)
            ? detalle
            : throw new AccesoDenegadoException("No tienes permiso para ver este documento todavía.");
    }

    public async Task CambiarSubAreaAsync(Actor actor, int documentoId, int? subAreaId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var documento = await db.Documentos.Include(d => d.Area).Include(d => d.SubArea)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (!actor.PuedeEditarEnArea(documento.Area!.Codigo))
        {
            throw new AccesoDenegadoException($"No tienes permiso para modificar documentos del área {documento.Area.Codigo}.");
        }

        // La subárea es la «unidad» del código: una vez asignada no cambia (el código tampoco). Esto solo permite asignarla
        // a un documento anterior al estándar de código, que no tiene unidad.
        if (documento.SubAreaId is not null)
        {
            throw new ReglaNegocioException(
                $"La subárea de {documento.Codigo} forma parte de su código: no se puede cambiar. Si está mal clasificado, retíralo y crea uno nuevo.");
        }

        SubArea? nueva = null;
        if (subAreaId is null)
        {
            throw new ReglaNegocioException("Elige la subárea que le corresponde al documento.");
        }

        if (subAreaId is { } id)
        {
            nueva = await db.SubAreas.FirstOrDefaultAsync(s => s.Id == id, ct);
            if (nueva is null || nueva.AreaId != documento.AreaId)
            {
                throw new ReglaNegocioException("La subárea no pertenece al área del documento.");
            }

            if (!nueva.Activa)
            {
                throw new ReglaNegocioException("La subárea está desactivada.");
            }
        }

        var anterior = documento.SubArea?.Nombre ?? "(sin subárea)";
        documento.SubAreaId = nueva?.Id;
        Bitacora.Registrar(db, reloj, actor, "Documento.CambiarSubArea", nameof(Documento), documento.Codigo,
            anterior, nueva?.Nombre ?? "(sin subárea)");

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            // El consecutivo de este documento ya lo usa otro de esa unidad (se numeraron por separado).
            throw new ReglaNegocioException(
                $"No se puede asignar esa subárea: ya hay un documento del mismo tipo con el consecutivo {documento.Consecutivo:000} en ella.");
        }
    }

    // ---------- Responsable ----------

    /// <summary>
    /// Puede quedar como responsable quien está activo y es Administrador o Autor del área del documento. Se apoya en la copia
    /// de roles y áreas del último acceso (no hay forma de consultar AD desde aquí): por eso solo aparecen quienes ya entraron.
    /// </summary>
    private static bool PuedeSerResponsable(Usuario u, string codigoArea) =>
        u.Activo
        && (u.TuvoRolEnUltimoAcceso(RolSistema.Administrador)
            || (u.TuvoRolEnUltimoAcceso(RolSistema.Autor)
                && u.AreasUltimoAcceso.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(codigoArea, StringComparer.OrdinalIgnoreCase)));

    public async Task<IReadOnlyList<CandidatoResponsableDto>> CandidatosResponsableAsync(Actor actor, int documentoId, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var documento = await db.Documentos.AsNoTracking().Include(d => d.Area)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        var usuarios = await db.Usuarios.AsNoTracking().Where(u => u.Activo && u.Id != documento.ResponsableId).ToListAsync(ct);

        return [.. usuarios.Where(u => PuedeSerResponsable(u, documento.Area!.Codigo))
            .OrderBy(u => u.NombreVisible)
            .Select(u => new CandidatoResponsableDto(u.Id, u.NombreVisible, u.Identificador))];
    }

    public async Task CambiarResponsableAsync(Actor actor, int documentoId, Guid nuevoResponsableId, string motivo, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);

        var texto = (motivo ?? "").Trim();
        if (texto.Length == 0)
        {
            throw new ReglaNegocioException("Indica el motivo del cambio de responsable.");
        }

        if (texto.Length > MaximoMotivo)
        {
            throw new ReglaNegocioException($"El motivo no puede pasar de {MaximoMotivo} caracteres.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var documento = await db.Documentos.Include(d => d.Area).Include(d => d.Responsable)
            .FirstOrDefaultAsync(d => d.Id == documentoId, ct)
            ?? throw new ReglaNegocioException("El documento no existe.");

        if (documento.Estado != EstadoDocumento.Activo)
        {
            throw new ReglaNegocioException("El documento está retirado: ya no tiene responsable que atender.");
        }

        if (documento.ResponsableId == nuevoResponsableId)
        {
            throw new ReglaNegocioException("Esa persona ya es la responsable del documento.");
        }

        var nuevo = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == nuevoResponsableId, ct)
            ?? throw new ReglaNegocioException("La persona elegida no existe.");

        if (!PuedeSerResponsable(nuevo, documento.Area!.Codigo))
        {
            throw new ReglaNegocioException(
                $"{nuevo.NombreVisible} no puede ser responsable de este documento: debe estar activa y ser Autor del área {documento.Area.Codigo} o Administrador.");
        }

        var anterior = documento.Responsable!;
        documento.ResponsableId = nuevo.Id;

        Bitacora.Registrar(db, reloj, actor, "Documento.CambiarResponsable", nameof(Documento), documento.Codigo,
            anterior: $"{anterior.NombreVisible} ({anterior.Identificador})", nuevo: $"{nuevo.NombreVisible} ({nuevo.Identificador})",
            comentario: texto);

        // Avisan la persona nueva y la anterior, salvo que sea quien hace el cambio. Mismo guardado que el cambio: si falla, no sale nada.
        var valores = notificador.ValoresBase(documento, "");
        valores["Actor"] = await notificador.NombreVisibleAsync(db, actor.Identificador, ct);
        valores["Motivo"] = texto;
        valores["Responsable"] = nuevo.NombreVisible;
        await notificador.EncolarAsync(db, EventosCorreo.ResponsableAsignado, nuevo.Id, valores, documento.Codigo,
            omitirIdentificador: actor.Identificador, ct: ct);
        await notificador.EncolarAsync(db, EventosCorreo.ResponsableRetirado, anterior.Id, valores, documento.Codigo,
            omitirIdentificador: actor.Identificador, ct: ct);

        await db.SaveChangesAsync(ct);
    }

    private static void ExigirAdministrador(Actor actor)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede cambiar el responsable de un documento.");
        }
    }

    private static async Task<DocumentoDetalleDto?> ObtenerInternoAsync(AppDbContext db, int id, CancellationToken ct)
    {
        var d = await db.Documentos.AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id,
                x.Codigo,
                x.Titulo,
                x.AreaId,
                CodigoArea = x.Area!.Codigo,
                Area = x.Area.Codigo + " – " + x.Area.Nombre,
                x.SubAreaId,
                SubArea = x.SubArea != null ? x.SubArea.Nombre : null,
                Tipo = x.TipoDocumento!.Nombre,
                x.Estado,
                Responsable = x.Responsable!.NombreVisible,
                x.FechaCreacionUtc,
                Revisiones = x.Revisiones
                    .OrderByDescending(r => r.Mayor).ThenByDescending(r => r.Menor)
                    .Select(r => new { r.Id, r.Mayor, r.Menor, r.Estado, r.MotivoCambio, Autor = r.Autor!.NombreVisible, r.FechaCreacionUtc })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        if (d is null)
        {
            return null;
        }

        return new DocumentoDetalleDto(
            d.Id, d.Codigo, d.Titulo, d.AreaId, d.CodigoArea, d.Area, d.SubAreaId, d.SubArea, d.Tipo,
            d.Estado, d.Responsable, d.FechaCreacionUtc,
            d.Revisiones.Select(r => new RevisionDto(
                r.Id, RevisionDocumento.FormatearEtiqueta(r.Mayor, r.Menor), r.Estado, r.MotivoCambio, r.Autor, r.FechaCreacionUtc)).ToList());
    }
}
