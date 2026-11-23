using System.Globalization;
using System.Text.RegularExpressions;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Infrastructure.Consulta;

internal sealed partial class ConsultaService(IDbContextFactory<AppDbContext> fabrica) : IConsultaService
{
    /// <summary>
    /// Sin distinguir mayúsculas ni acentos, sea cual sea la intercalación de la base de datos. Se indica
    /// en cada comparación para que «calibracion» encuentre «Calibración» igual en desarrollo y producción.
    /// </summary>
    private const string Intercalacion = "Latin1_General_100_CI_AI";

    private const string CaracterDeEscape = "\\";

    [GeneratedRegex(@"^Rev\.(\d+)(?:\.(\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PatronEtiqueta();

    // ---------- Resumen para el Home ----------

    public async Task<ResumenCatalogoDto> ResumenCatalogoAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);

        // Una sola pasada sobre lo vigente: cuántos hay por área, subárea y tipo.
        var vigentes = await VigentesDeDocumentosActivos(db)
            .Select(r => new { r.Documento!.AreaId, r.Documento.SubAreaId, r.Documento.TipoDocumentoId })
            .ToListAsync(ct);

        var porArea = vigentes.GroupBy(v => v.AreaId).ToDictionary(g => g.Key, g => g.Count());
        var porSub = vigentes.Where(v => v.SubAreaId != null).GroupBy(v => v.SubAreaId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var porTipo = vigentes.GroupBy(v => v.TipoDocumentoId).ToDictionary(g => g.Key, g => g.Count());

        var areas = await db.Areas.AsNoTracking().Where(a => a.Activa)
            .Include(a => a.SubAreas.Where(s => s.Activa))
            .OrderBy(a => a.Nombre)
            .AsSplitQuery()
            .ToListAsync(ct);

        var tipos = await db.TiposDocumento.AsNoTracking().Where(t => t.Activo).OrderBy(t => t.Codigo).ToListAsync(ct);

        return new ResumenCatalogoDto(
            [.. areas.Select(a => new ResumenAreaDto(
                a.Id, a.Codigo, a.Nombre, porArea.GetValueOrDefault(a.Id),
                [.. a.SubAreas.OrderBy(s => s.Nombre).Select(s => new ResumenSubAreaDto(s.Id, s.Codigo, s.Nombre, porSub.GetValueOrDefault(s.Id)))]))],
            [.. tipos.Select(t => new ResumenTipoDto(t.Id, t.Codigo, t.Nombre, porTipo.GetValueOrDefault(t.Id)))]);
    }

    // ---------- Lista maestra ----------

    public async Task<Pagina<FilaListaMaestra>> ListaMaestraAsync(FiltroListaMaestra filtro, int pagina = 1, int tamano = 25, CancellationToken ct = default)
    {
        var (numero, tam) = ReglasBusqueda.NormalizarPagina(pagina, tamano, 25);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var consulta = AplicarFiltro(VigentesDeDocumentosActivos(db), filtro);

        var total = await consulta.CountAsync(ct);
        var filas = await ProyectarLista(consulta).Skip((numero - 1) * tam).Take(tam).ToListAsync(ct);

        return new Pagina<FilaListaMaestra>(filas.Select(AFila).ToList(), numero, tam, total);
    }

    public async Task<IReadOnlyList<FilaListaMaestra>> ListaMaestraCompletaAsync(FiltroListaMaestra filtro, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var filas = await ProyectarLista(AplicarFiltro(VigentesDeDocumentosActivos(db), filtro))
            .Take(ReglasBusqueda.MaximoFilasExportacion)
            .ToListAsync(ct);

        return filas.Select(AFila).ToList();
    }

    /// <summary>Lo único que ve un lector: la revisión vigente de documentos activos.</summary>
    private static IQueryable<RevisionDocumento> VigentesDeDocumentosActivos(AppDbContext db) =>
        db.Revisiones.AsNoTracking()
            .Where(r => r.Estado == EstadoRevision.Vigente && r.Documento!.Estado == EstadoDocumento.Activo);

    private static IQueryable<RevisionDocumento> AplicarFiltro(IQueryable<RevisionDocumento> consulta, FiltroListaMaestra filtro)
    {
        if (filtro.AreaId is { } area)
        {
            consulta = consulta.Where(r => r.Documento!.AreaId == area);
        }

        if (filtro.SubAreaId is { } sub)
        {
            consulta = consulta.Where(r => r.Documento!.SubAreaId == sub);
        }

        if (filtro.TipoDocumentoId is { } tipo)
        {
            consulta = consulta.Where(r => r.Documento!.TipoDocumentoId == tipo);
        }

        if (!string.IsNullOrWhiteSpace(filtro.Texto))
        {
            // En la lista maestra el texto filtra solo por código y título (rápido); el contenido se busca en «Buscar».
            foreach (var termino in ReglasBusqueda.Terminos(filtro.Texto))
            {
                var patron = $"%{ReglasBusqueda.EscaparLike(termino)}%";
                consulta = consulta.Where(r =>
                    EF.Functions.Like(EF.Functions.Collate(r.Documento!.Codigo, Intercalacion), patron, CaracterDeEscape)
                    || EF.Functions.Like(EF.Functions.Collate(r.Documento.Titulo, Intercalacion), patron, CaracterDeEscape));
            }
        }

        return consulta;
    }

    private sealed record FilaCruda(
        int DocumentoId, int RevisionId, string Codigo, string Titulo, string Area, string? SubArea, string Tipo,
        int Mayor, int Menor, DateTime? Aprobacion, DateTime? Vigencia, DateTime? Proxima, string Responsable);

    private static IQueryable<FilaCruda> ProyectarLista(IQueryable<RevisionDocumento> consulta) =>
        consulta.OrderBy(r => r.Documento!.Codigo)
            .Select(r => new FilaCruda(
                r.DocumentoId, r.Id, r.Documento!.Codigo, r.Documento.Titulo,
                r.Documento.Area!.Codigo + " – " + r.Documento.Area.Nombre,
                r.Documento.SubArea != null ? r.Documento.SubArea.Nombre : null,
                r.Documento.TipoDocumento!.Nombre,
                r.Mayor, r.Menor, r.FechaAprobacionUtc, r.FechaVigenciaUtc, r.FechaProximaRevisionUtc,
                r.Documento.Responsable!.NombreVisible));

    private static FilaListaMaestra AFila(FilaCruda f) =>
        new(f.DocumentoId, f.RevisionId, f.Codigo, f.Titulo, f.Area, f.SubArea, f.Tipo,
            RevisionDocumento.FormatearEtiqueta(f.Mayor, f.Menor), f.Aprobacion, f.Vigencia, f.Proxima, f.Responsable);

    // ---------- Búsqueda ----------

    public async Task<Pagina<ResultadoBusqueda>> BuscarAsync(Actor actor, ConsultaBusqueda consulta, int pagina = 1, int tamano = 20, CancellationToken ct = default)
    {
        var terminos = ReglasBusqueda.Terminos(consulta.Texto);
        var (numero, tam) = ReglasBusqueda.NormalizarPagina(pagina, tamano, 20);

        // Las revisiones obsoletas no son de lectura abierta: solo Administrador y Auditor pueden incluirlas.
        if (consulta.IncluirObsoletos && !(actor.EsAdministrador || actor.TieneRol(RolSistema.Auditor)))
        {
            throw new AccesoDenegadoException("Solo un Administrador o un Auditor puede buscar en revisiones obsoletas.");
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);

        var baseQ = consulta.IncluirObsoletos
            ? db.Revisiones.AsNoTracking().Where(r => r.Estado == EstadoRevision.Vigente || r.Estado == EstadoRevision.Obsoleto)
            : VigentesDeDocumentosActivos(db);

        if (consulta.AreaId is { } area)
        {
            baseQ = baseQ.Where(r => r.Documento!.AreaId == area);
        }

        if (consulta.TipoDocumentoId is { } tipo)
        {
            baseQ = baseQ.Where(r => r.Documento!.TipoDocumentoId == tipo);
        }

        // Todas las palabras deben aparecer, cada una en el código, el título o el contenido.
        foreach (var termino in terminos)
        {
            var patron = $"%{ReglasBusqueda.EscaparLike(termino)}%";
            baseQ = baseQ.Where(r =>
                EF.Functions.Like(EF.Functions.Collate(r.Documento!.Codigo, Intercalacion), patron, CaracterDeEscape)
                || EF.Functions.Like(EF.Functions.Collate(r.Documento.Titulo, Intercalacion), patron, CaracterDeEscape)
                || (r.TextoExtraido != null
                    && EF.Functions.Like(EF.Functions.Collate(r.TextoExtraido, Intercalacion), patron, CaracterDeEscape)));
        }

        // Primero solo lo liviano (sin el texto de los PDF) para ordenar y paginar...
        var candidatos = await baseQ
            .Select(r => new { r.Id, r.Documento!.Codigo, r.Documento.Titulo, r.Estado, r.Mayor, r.Menor })
            .ToListAsync(ct);

        // ...y se ordena por relevancia: primero lo que coincide en código o título, luego solo en el contenido.
        var ordenados = candidatos
            .Select(c => new { Candidato = c, EnMeta = CoincideTodoEn(terminos, c.Codigo, c.Titulo) })
            .OrderBy(x => x.EnMeta ? 0 : 1)
            .ThenBy(x => x.Candidato.Codigo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Candidato.Estado == EstadoRevision.Vigente ? 0 : 1)
            .ThenByDescending(x => x.Candidato.Mayor)
            .ThenByDescending(x => x.Candidato.Menor)
            .ToList();

        var idsPagina = ordenados.Skip((numero - 1) * tam).Take(tam).Select(x => x.Candidato.Id).ToList();

        // ...y el texto completo solo de la página que se va a mostrar.
        var detalle = await db.Revisiones.AsNoTracking()
            .Where(r => idsPagina.Contains(r.Id))
            .Select(r => new
            {
                r.Id, r.DocumentoId, r.Documento!.Codigo, r.Documento.Titulo,
                Area = r.Documento.Area!.Codigo + " – " + r.Documento.Area.Nombre,
                Tipo = r.Documento.TipoDocumento!.Nombre,
                r.Mayor, r.Menor, r.Estado, r.TextoExtraido,
            })
            .ToDictionaryAsync(r => r.Id, ct);

        var enMeta = ordenados.ToDictionary(x => x.Candidato.Id, x => x.EnMeta);
        var resultados = idsPagina.Select(id =>
        {
            var d = detalle[id];
            return new ResultadoBusqueda(
                d.DocumentoId, d.Id, d.Codigo, d.Titulo, d.Area, d.Tipo,
                RevisionDocumento.FormatearEtiqueta(d.Mayor, d.Menor), d.Estado, enMeta[id],
                ReglasBusqueda.Fragmento(d.TextoExtraido, terminos));
        }).ToList();

        return new Pagina<ResultadoBusqueda>(resultados, numero, tam, ordenados.Count);
    }

    /// <summary>Cada palabra aparece en el código o en el título (misma comparación que la consulta).</summary>
    private static bool CoincideTodoEn(IReadOnlyList<string> terminos, string codigo, string titulo)
    {
        var comparador = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions Opciones = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        return terminos.All(t => comparador.IndexOf(codigo, t, Opciones) >= 0 || comparador.IndexOf(titulo, t, Opciones) >= 0);
    }

    // ---------- Visor ----------

    public async Task<DocumentoVigenteDto?> ObtenerVigenteAsync(string codigo, CancellationToken ct = default)
    {
        var clave = (codigo ?? "").Trim().ToUpperInvariant();
        if (clave.Length == 0)
        {
            return null;
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var r = await VigentesDeDocumentosActivos(db)
            .Where(x => x.Documento!.Codigo == clave)
            .Select(x => new
            {
                x.DocumentoId, x.Id, x.Documento!.Codigo, x.Documento.Titulo,
                Area = x.Documento.Area!.Codigo + " – " + x.Documento.Area.Nombre,
                CodigoArea = x.Documento.Area.Codigo,
                SubArea = x.Documento.SubArea != null ? x.Documento.SubArea.Nombre : null,
                Tipo = x.Documento.TipoDocumento!.Nombre,
                x.Mayor, x.Menor, x.FechaAprobacionUtc, x.FechaVigenciaUtc, x.FechaProximaRevisionUtc,
                Responsable = x.Documento.Responsable!.NombreVisible,
            })
            .FirstOrDefaultAsync(ct);

        return r is null
            ? null
            : new DocumentoVigenteDto(
                r.DocumentoId, r.Id, r.Codigo, r.Titulo, r.Area, r.CodigoArea, r.SubArea, r.Tipo,
                RevisionDocumento.FormatearEtiqueta(r.Mayor, r.Menor),
                r.FechaAprobacionUtc, r.FechaVigenciaUtc, r.FechaProximaRevisionUtc, r.Responsable);
    }

    public async Task<(int DocumentoId, int RevisionId)?> ResolverRevisionAsync(string codigo, string etiqueta, CancellationToken ct = default)
    {
        var clave = (codigo ?? "").Trim().ToUpperInvariant();
        var m = PatronEtiqueta().Match((etiqueta ?? "").Trim());
        if (clave.Length == 0 || !m.Success
            || !int.TryParse(m.Groups[1].Value, out var mayor)
            || !int.TryParse(m.Groups[2].Success ? m.Groups[2].Value : "0", out var menor))
        {
            return null;
        }

        await using var db = await fabrica.CreateDbContextAsync(ct);
        // Las canceladas se excluyen: su número puede haberse reutilizado.
        var r = await db.Revisiones.AsNoTracking()
            .Where(x => x.Documento!.Codigo == clave && x.Mayor == mayor && x.Menor == menor && x.Estado != EstadoRevision.Cancelado)
            .Select(x => new { x.DocumentoId, x.Id })
            .FirstOrDefaultAsync(ct);

        return r is null ? null : (r.DocumentoId, r.Id);
    }
}
