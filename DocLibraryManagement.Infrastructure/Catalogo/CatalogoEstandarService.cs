using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Catalogo;

/// <summary>
/// Carga el catálogo estándar de Calidad (<see cref="CatalogoEstandar"/>). Solo agrega lo que falta: no toca nombres, activación,
/// dueños, asociaciones ni plantillas que ya existan, de modo que se puede ejecutar cuantas veces haga falta.
/// </summary>
internal sealed class CatalogoEstandarService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : ICatalogoEstandarService
{
    public async Task<ResultadoCargaCatalogo> CargarAsync(Actor actor, CancellationToken ct = default)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede cargar el catálogo estándar.");
        }

        var errores = CatalogoEstandar.Validar();
        if (errores.Count > 0)
        {
            throw new InvalidOperationException("El catálogo estándar no es válido: " + string.Join(" ", errores));
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var tiposNuevos = 0;
        var areasNuevas = 0;
        var subAreasNuevas = 0;
        var asociaciones = 0;
        var plantillas = 0;

        // 1) Tipos documentales.
        var tipos = await db.TiposDocumento.ToDictionaryAsync(t => t.Codigo, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var t in CatalogoEstandar.Tipos.Where(t => !tipos.ContainsKey(t.Codigo)))
        {
            var nuevo = new TipoDocumento { Codigo = t.Codigo, Nombre = t.Nombre, MesesRevision = CatalogoEstandar.MesesRevision };
            db.TiposDocumento.Add(nuevo);
            tipos[t.Codigo] = nuevo;
            tiposNuevos++;
        }

        // 2) Áreas y subáreas (unidades). Se busca por abreviatura: no se renombra nada que ya exista.
        var areas = await db.Areas.Include(a => a.SubAreas).Include(a => a.TiposDocumento)
            .ToDictionaryAsync(a => a.Codigo, StringComparer.OrdinalIgnoreCase, ct);
        foreach (var estandar in CatalogoEstandar.Areas)
        {
            if (!areas.TryGetValue(estandar.Codigo, out var area))
            {
                area = new Area { Codigo = estandar.Codigo, Nombre = estandar.Nombre };
                db.Areas.Add(area);
                areas[estandar.Codigo] = area;
                areasNuevas++;
            }

            foreach (var u in estandar.Unidades)
            {
                var existente = area.SubAreas.FirstOrDefault(s => string.Equals(s.Codigo, u.Codigo, StringComparison.OrdinalIgnoreCase));

                // Una subárea anterior con el mismo nombre pero sin abreviatura se completa en lugar de duplicarla.
                existente ??= area.SubAreas.FirstOrDefault(s => s.Codigo is null && string.Equals(s.Nombre, u.Nombre, StringComparison.OrdinalIgnoreCase));
                if (existente is { Codigo: null })
                {
                    existente.Codigo = u.Codigo;
                    subAreasNuevas++;
                    continue;
                }

                if (existente is null)
                {
                    area.SubAreas.Add(new SubArea { Codigo = u.Codigo, Nombre = u.Nombre });
                    subAreasNuevas++;
                }
            }
        }

        // Los ids de lo recién creado se conocen hasta guardar; las asociaciones y plantillas los necesitan.
        await db.SaveChangesAsync(ct);

        // 3) Qué tipos aplican en cada área (todos) y las excepciones por unidad (p. ej. Metrología sin Plan de Inspección).
        foreach (var estandar in CatalogoEstandar.Areas)
        {
            var area = areas[estandar.Codigo];
            var ya = area.TiposDocumento.Select(t => t.TipoDocumentoId).ToHashSet();
            foreach (var t in CatalogoEstandar.Tipos.Select(t => tipos[t.Codigo]).Where(t => !ya.Contains(t.Id)))
            {
                area.TiposDocumento.Add(new AreaTipoDocumento { AreaId = area.Id, TipoDocumentoId = t.Id });
                asociaciones++;
            }

            foreach (var ((_, codUnidad), noAplican) in CatalogoEstandar.TiposNoAplicables.Where(e => e.Key.Area == estandar.Codigo))
            {
                var sub = area.SubAreas.FirstOrDefault(s => string.Equals(s.Codigo, codUnidad, StringComparison.OrdinalIgnoreCase));
                if (sub is null || await db.SubAreasTiposDocumento.AnyAsync(x => x.SubAreaId == sub.Id, ct))
                {
                    continue; // si el Administrador ya definió los tipos de esa unidad, no se sobrescribe
                }

                foreach (var t in CatalogoEstandar.Tipos.Where(t => !noAplican.Contains(t.Codigo)))
                {
                    db.SubAreasTiposDocumento.Add(new SubAreaTipoDocumento { SubAreaId = sub.Id, TipoDocumentoId = tipos[t.Codigo].Id });
                    asociaciones++;
                }
            }
        }

        // 4) Plantilla de flujo por omisión (Revisión → Aprobación) para los tipos que no tengan ninguna.
        var conPlantilla = await db.PasosPlantilla.Select(p => p.TipoDocumentoId).Distinct().ToListAsync(ct);
        foreach (var t in CatalogoEstandar.Tipos.Select(t => tipos[t.Codigo]).Where(t => !conPlantilla.Contains(t.Id)))
        {
            foreach (var (nombre, orden, esAprobacion) in CatalogoEstandar.FlujoPorOmision)
            {
                db.PasosPlantilla.Add(new PasoPlantilla
                {
                    TipoDocumentoId = t.Id,
                    Orden = orden,
                    Clase = esAprobacion ? TipoPaso.Aprobacion : TipoPaso.Revision,
                    Nombre = nombre,
                });
            }

            plantillas++;
        }

        var resultado = new ResultadoCargaCatalogo(areasNuevas, subAreasNuevas, tiposNuevos, asociaciones, plantillas);
        Bitacora.Registrar(db, reloj, actor, "Catalogo.CargarEstandar", "Catalogo", "estandar",
            nuevo: $"Areas={areasNuevas}; Subareas={subAreasNuevas}; Tipos={tiposNuevos}; Asociaciones={asociaciones}; Plantillas={plantillas}",
            comentario: resultado.SinCambios ? "El catálogo estándar ya estaba cargado." : null);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return resultado;
    }
}
