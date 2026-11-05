using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Catalogo;

internal sealed class CatalogoService(IDbContextFactory<AppDbContext> fabrica, TimeProvider reloj) : ICatalogoService
{
    private static void ExigirAdministrador(Actor actor)
    {
        if (!actor.EsAdministrador)
        {
            throw new AccesoDenegadoException("Solo un Administrador puede modificar el catálogo.");
        }
    }

    // ---------- Consultas ----------

    public async Task<IReadOnlyList<AreaDto>> ListarAreasAsync(bool incluirInactivas = false, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var areas = await db.Areas.AsNoTracking()
            .Where(a => incluirInactivas || a.Activa)
            .Include(a => a.SubAreas.Where(s => incluirInactivas || s.Activa)).ThenInclude(s => s.TiposDocumento)
            .Include(a => a.TiposDocumento)
            .OrderBy(a => a.Codigo)
            .AsSplitQuery()
            .ToListAsync(ct);

        return areas.Select(a => new AreaDto(
            a.Id, a.Codigo, a.Nombre, a.Activa,
            a.SubAreas.OrderBy(s => s.Nombre)
                .Select(s => new SubAreaDto(s.Id, s.AreaId, s.Nombre, s.Activa, s.TiposDocumento.Select(t => t.TipoDocumentoId).ToList()))
                .ToList(),
            a.TiposDocumento.Select(t => t.TipoDocumentoId).ToList())).ToList();
    }

    public async Task<IReadOnlyList<TipoDocumentoDto>> ListarTiposAsync(bool incluirInactivos = false, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        return await db.TiposDocumento.AsNoTracking()
            .Where(t => incluirInactivos || t.Activo)
            .OrderBy(t => t.Codigo)
            .Select(t => new TipoDocumentoDto(t.Id, t.Codigo, t.Nombre, t.MesesRevision, t.Activo))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TipoDocumentoDto>> TiposDisponiblesAsync(int areaId, int? subAreaId, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        return await TiposDisponiblesAsync(db, areaId, subAreaId, ct);
    }

    /// <summary>Compartido con la creación de documentos para que ambas pantallas apliquen la misma regla.</summary>
    internal static async Task<IReadOnlyList<TipoDocumentoDto>> TiposDisponiblesAsync(
        AppDbContext db, int areaId, int? subAreaId, CancellationToken ct)
    {
        var delArea = await db.AreasTiposDocumento.Where(x => x.AreaId == areaId)
            .Select(x => x.TipoDocumentoId).ToListAsync(ct);

        IReadOnlyCollection<int>? propios = null;
        if (subAreaId is { } sub)
        {
            propios = await db.SubAreasTiposDocumento.Where(x => x.SubAreaId == sub)
                .Select(x => x.TipoDocumentoId).ToListAsync(ct);
        }

        var ids = ReglasCatalogo.TiposEfectivos(delArea, propios);

        return await db.TiposDocumento.AsNoTracking()
            .Where(t => t.Activo && ids.Contains(t.Id))
            .OrderBy(t => t.Codigo)
            .Select(t => new TipoDocumentoDto(t.Id, t.Codigo, t.Nombre, t.MesesRevision, t.Activo))
            .ToListAsync(ct);
    }

    // ---------- Áreas ----------

    public async Task<int> CrearAreaAsync(Actor actor, string codigo, string nombre, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var cod = ReglasCatalogo.NormalizarCodigo(codigo, "el área");
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "el área");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        if (await db.Areas.AnyAsync(a => a.Codigo == cod, ct))
        {
            throw new ReglaNegocioException($"Ya existe un área con la abreviatura {cod}.");
        }

        var area = new Area { Codigo = cod, Nombre = nom };
        db.Areas.Add(area);
        Bitacora.Registrar(db, reloj, actor, "Area.Crear", nameof(Area), cod, nuevo: $"Codigo={cod}; Nombre={nom}");

        await GuardarAsync(db, $"Ya existe un área con la abreviatura {cod}.", ct);
        return area.Id;
    }

    public async Task ActualizarAreaAsync(Actor actor, int areaId, string nombre, bool activa, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "el área");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == areaId, ct)
            ?? throw new ReglaNegocioException("El área no existe.");

        var anterior = $"Nombre={area.Nombre}; Activa={area.Activa}";
        area.Nombre = nom;
        area.Activa = activa;
        Bitacora.Registrar(db, reloj, actor, "Area.Actualizar", nameof(Area), area.Codigo,
            anterior, $"Nombre={nom}; Activa={activa}");

        await db.SaveChangesAsync(ct);
    }

    // ---------- Subáreas ----------

    public async Task<int> CrearSubAreaAsync(Actor actor, int areaId, string nombre, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "la subárea");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var area = await db.Areas.FirstOrDefaultAsync(a => a.Id == areaId, ct)
            ?? throw new ReglaNegocioException("El área no existe.");

        if (await db.SubAreas.AnyAsync(s => s.AreaId == areaId && s.Nombre == nom, ct))
        {
            throw new ReglaNegocioException($"El área {area.Codigo} ya tiene una subárea llamada «{nom}».");
        }

        var sub = new SubArea { AreaId = areaId, Nombre = nom };
        db.SubAreas.Add(sub);

        // El Id de la subárea se conoce hasta guardar: ambos pasos van en una sola transacción.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await GuardarAsync(db, $"El área {area.Codigo} ya tiene una subárea llamada «{nom}».", ct);
        Bitacora.Registrar(db, reloj, actor, "SubArea.Crear", nameof(SubArea), sub.Id.ToString(),
            nuevo: $"Area={area.Codigo}; Nombre={nom}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return sub.Id;
    }

    public async Task ActualizarSubAreaAsync(Actor actor, int subAreaId, string nombre, bool activa, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "la subárea");

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var sub = await db.SubAreas.FirstOrDefaultAsync(s => s.Id == subAreaId, ct)
            ?? throw new ReglaNegocioException("La subárea no existe.");

        if (await db.SubAreas.AnyAsync(s => s.AreaId == sub.AreaId && s.Nombre == nom && s.Id != subAreaId, ct))
        {
            throw new ReglaNegocioException($"Ya existe otra subárea llamada «{nom}» en esta área.");
        }

        var anterior = $"Nombre={sub.Nombre}; Activa={sub.Activa}";
        sub.Nombre = nom;
        sub.Activa = activa;
        Bitacora.Registrar(db, reloj, actor, "SubArea.Actualizar", nameof(SubArea), sub.Id.ToString(),
            anterior, $"Nombre={nom}; Activa={activa}");

        await db.SaveChangesAsync(ct);
    }

    // ---------- Tipos de documento ----------

    public async Task<int> CrearTipoAsync(Actor actor, string codigo, string nombre, int mesesRevision, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var cod = ReglasCatalogo.NormalizarCodigo(codigo, "el tipo de documento");
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "el tipo de documento");
        ValidarMeses(mesesRevision);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        if (await db.TiposDocumento.AnyAsync(t => t.Codigo == cod, ct))
        {
            throw new ReglaNegocioException($"Ya existe un tipo de documento con la abreviatura {cod}.");
        }

        var tipo = new TipoDocumento { Codigo = cod, Nombre = nom, MesesRevision = mesesRevision };
        db.TiposDocumento.Add(tipo);
        Bitacora.Registrar(db, reloj, actor, "Tipo.Crear", nameof(TipoDocumento), cod,
            nuevo: $"Codigo={cod}; Nombre={nom}; MesesRevision={mesesRevision}");

        await GuardarAsync(db, $"Ya existe un tipo de documento con la abreviatura {cod}.", ct);
        return tipo.Id;
    }

    public async Task ActualizarTipoAsync(Actor actor, int tipoId, string nombre, int mesesRevision, bool activo, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        var nom = ReglasCatalogo.NormalizarNombre(nombre, "el tipo de documento");
        ValidarMeses(mesesRevision);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var tipo = await db.TiposDocumento.FirstOrDefaultAsync(t => t.Id == tipoId, ct)
            ?? throw new ReglaNegocioException("El tipo de documento no existe.");

        var anterior = $"Nombre={tipo.Nombre}; MesesRevision={tipo.MesesRevision}; Activo={tipo.Activo}";
        tipo.Nombre = nom;
        tipo.MesesRevision = mesesRevision;
        tipo.Activo = activo;
        Bitacora.Registrar(db, reloj, actor, "Tipo.Actualizar", nameof(TipoDocumento), tipo.Codigo,
            anterior, $"Nombre={nom}; MesesRevision={mesesRevision}; Activo={activo}");

        await db.SaveChangesAsync(ct);
    }

    private static void ValidarMeses(int meses)
    {
        if (meses is < 1 or > 120)
        {
            throw new ReglaNegocioException("Los meses de revisión periódica deben estar entre 1 y 120.");
        }
    }

    // ---------- Asociaciones ----------

    public async Task EstablecerTiposDeAreaAsync(Actor actor, int areaId, IReadOnlyCollection<int> tipoIds, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var area = await db.Areas.Include(a => a.TiposDocumento).FirstOrDefaultAsync(a => a.Id == areaId, ct)
            ?? throw new ReglaNegocioException("El área no existe.");

        var nuevos = tipoIds.Distinct().ToHashSet();
        var codigos = await CodigosDeTiposAsync(db, nuevos, ct);
        var anteriores = await CodigosDeTiposAsync(db, area.TiposDocumento.Select(t => t.TipoDocumentoId).ToHashSet(), ct);

        area.TiposDocumento.RemoveAll(t => !nuevos.Contains(t.TipoDocumentoId));
        var existentes = area.TiposDocumento.Select(t => t.TipoDocumentoId).ToHashSet();
        foreach (var id in nuevos.Where(id => !existentes.Contains(id)))
        {
            area.TiposDocumento.Add(new AreaTipoDocumento { AreaId = areaId, TipoDocumentoId = id });
        }

        Bitacora.Registrar(db, reloj, actor, "Area.TiposAsociados", nameof(Area), area.Codigo,
            string.Join(',', anteriores), string.Join(',', codigos));
        await db.SaveChangesAsync(ct);
    }

    public async Task EstablecerTiposDeSubAreaAsync(Actor actor, int subAreaId, IReadOnlyCollection<int> tipoIds, CancellationToken ct = default)
    {
        ExigirAdministrador(actor);
        await using var db = await fabrica.CreateDbContextAsync(ct);

        var sub = await db.SubAreas.Include(s => s.TiposDocumento).FirstOrDefaultAsync(s => s.Id == subAreaId, ct)
            ?? throw new ReglaNegocioException("La subárea no existe.");

        var nuevos = tipoIds.Distinct().ToHashSet();
        var codigos = await CodigosDeTiposAsync(db, nuevos, ct);
        var anteriores = await CodigosDeTiposAsync(db, sub.TiposDocumento.Select(t => t.TipoDocumentoId).ToHashSet(), ct);

        sub.TiposDocumento.RemoveAll(t => !nuevos.Contains(t.TipoDocumentoId));
        var existentes = sub.TiposDocumento.Select(t => t.TipoDocumentoId).ToHashSet();
        foreach (var id in nuevos.Where(id => !existentes.Contains(id)))
        {
            sub.TiposDocumento.Add(new SubAreaTipoDocumento { SubAreaId = subAreaId, TipoDocumentoId = id });
        }

        Bitacora.Registrar(db, reloj, actor, "SubArea.TiposAsociados", nameof(SubArea), sub.Id.ToString(),
            string.Join(',', anteriores), string.Join(',', codigos));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Valida que todos los ids existan y devuelve sus abreviaturas (para la bitácora).</summary>
    private static async Task<List<string>> CodigosDeTiposAsync(AppDbContext db, HashSet<int> ids, CancellationToken ct)
    {
        var tipos = await db.TiposDocumento.Where(t => ids.Contains(t.Id)).Select(t => t.Codigo).OrderBy(c => c).ToListAsync(ct);
        if (tipos.Count != ids.Count)
        {
            throw new ReglaNegocioException("Alguno de los tipos de documento indicados no existe.");
        }

        return tipos;
    }

    /// <summary>Guarda y traduce una violación de unicidad (carrera entre dos administradores) a un mensaje claro.</summary>
    private static async Task GuardarAsync(AppDbContext db, string mensajeDuplicado, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (Bitacora.EsViolacionUnica(ex))
        {
            throw new ReglaNegocioException(mensajeDuplicado);
        }
    }
}
