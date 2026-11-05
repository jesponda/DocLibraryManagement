using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Catalogo;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Documentos;

internal sealed class DocumentoService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : IDocumentoService
{
    private const int MaximoTitulo = 300;

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

            if (nuevo.SubAreaId is { } subId)
            {
                var sub = await db.SubAreas.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subId, ct);
                if (sub is null || sub.AreaId != area.Id)
                {
                    throw new ReglaNegocioException("La subárea no pertenece al área indicada.");
                }

                if (!sub.Activa)
                {
                    throw new ReglaNegocioException("La subárea está desactivada y no admite documentos nuevos.");
                }
            }

            var disponibles = await CatalogoService.TiposDisponiblesAsync(db, area.Id, nuevo.SubAreaId, ct);
            var tipo = disponibles.FirstOrDefault(t => t.Id == nuevo.TipoDocumentoId)
                ?? throw new ReglaNegocioException(
                    "Ese tipo de documento no está habilitado para el área o subárea elegida, o está desactivado.");

            var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Identificador == actor.Identificador, ct)
                ?? throw new ReglaNegocioException("Tu usuario aún no está registrado. Cierra sesión y vuelve a entrar.");

            var consecutivo = (await db.Documentos
                .Where(d => d.TipoDocumentoId == tipo.Id && d.AreaId == area.Id)
                .MaxAsync(d => (int?)d.Consecutivo, ct) ?? 0) + 1;

            var ahora = reloj.GetUtcNow().UtcDateTime;
            var documento = new Documento
            {
                Codigo = Documento.ConstruirCodigo(tipo.Codigo, area.Codigo, consecutivo),
                Titulo = titulo,
                AreaId = area.Id,
                SubAreaId = nuevo.SubAreaId,
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
                nuevo: $"Titulo={titulo}; Area={area.Codigo}; Tipo={tipo.Codigo}; Rev.00 Borrador");

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

        var puedeVer = actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor) || actor.PuedeEditarEnArea(detalle.CodigoArea);
        return puedeVer ? detalle : throw new AccesoDenegadoException("No tienes permiso para ver este documento todavía.");
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

        SubArea? nueva = null;
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

        await db.SaveChangesAsync(ct);
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
