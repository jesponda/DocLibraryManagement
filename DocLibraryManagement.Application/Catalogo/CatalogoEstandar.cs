using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Catalogo;

/// <summary>Una unidad del estándar: una subárea de nuestro catálogo (el último nivel que tiene código).</summary>
public sealed record UnidadEstandar(string Codigo, string Nombre);

public sealed record AreaEstandar(string Codigo, string Nombre, IReadOnlyList<UnidadEstandar> Unidades);

public sealed record TipoEstandar(string Codigo, string Nombre);

/// <summary>
/// Catálogo estándar de Allegion Querétaro, tomado de la estructura que definió Calidad: áreas, sus unidades y la
/// nomenclatura de tipos documentales. El estándar tiene tres niveles (Área → Subárea → Función); aquí las funciones se
/// cargan <b>aplanadas</b> como subáreas con el nombre «Subárea · Función» (p. ej. «Process Quality · Die Cast»), porque la
/// «unidad» del código del documento es el nivel más específico: la función, o la subárea cuando no tiene funciones.
/// </summary>
public static class CatalogoEstandar
{
    /// <summary>Política global: la revisión periódica es anual para todos los tipos.</summary>
    public const int MesesRevision = 12;

    /// <summary>Área transversal de planta: se muestra aparte en el Home.</summary>
    public const string AreaTransversal = "SGD";

    /// <summary>Orden de las tarjetas del Home (el resto de las áreas va después, por nombre).</summary>
    public static readonly IReadOnlyList<string> OrdenHome = ["QA", "PR", "ENG", "MT", "IT", "FN", "HR", "EHS"];

    private static readonly string[] Lineas = ["DC", "PM", "PA", "ES", "PL", "PW", "SE", "EN", "SA", "SM", "LI"];

    private static readonly string[] NombresLineas =
        ["Die Cast", "Pulido Manual", "Pulido Automático", "Estampado", "Platinado", "Powder", "Sub Ensamble", "Ensamble",
         "Satinado Automático", "Satinado Manual", "Lijado"];

    private static UnidadEstandar U(string codigo, string nombre) => new(codigo, nombre);

    private static IEnumerable<UnidadEstandar> DeLineas(string prefijoNombre = "") =>
        Lineas.Select((codigo, i) => U(codigo, prefijoNombre + NombresLineas[i]));

    public static readonly IReadOnlyList<TipoEstandar> Tipos =
    [
        new("P", "Procedimiento"), new("F", "Formato"), new("DP", "Descripción de Puesto"), new("CP", "Carta de Parámetros"),
        new("AE", "Análisis Ergonómico"), new("JSA", "Job Safety Analysis"), new("PFEP", "Plan For Every Part"),
        new("LOTO", "Lockout Tagout"), new("PO", "Políticas"), new("MC", "Manuales de calidad"), new("AV", "Ayuda visual"),
        new("HTE", "Hoja de Trabajo Estándar"), new("AC", "Alerta de Calidad"), new("PI", "Plan de Inspección"),
        new("CHL", "Check List"), new("DF", "Diagrama de Flujo"), new("PFM", "PFMEA"), new("PC", "Plan de Control"),
        new("R", "Registros"), new("DESV", "Desviaciones"),
    ];

    private static readonly IReadOnlyList<AreaEstandar> AreasDeCalidad =
    [
        new("QA", "Quality",
        [
            U("II", "Incoming Inspection"), U("MET", "Metrología"), U("SIP", "Shipping"),
            .. DeLineas("Process Quality · "),
        ]),
        new("PR", "Production", [.. DeLineas()]),
        new("ENG", "Engineering", [U("MNT", "Mantenimiento"), U("POE", "Procesos de ingeniería"), U("TOL", "Tooling")]),
        new("MT", "Materials",
        [
            U("PCT", "Production Control"), U("RCE", "Receipts"), U("SIP", "Shipments"), U("PRC", "Purchasing"),
            U("IVC", "Inventory Control"), U("MRO", "MRO"),
        ]),
        new("IT", "IT",
        [
            U("AP", "Information Technology · Applications"), U("DSG", "Information Technology · Design"),
            U("SPO", "Information Technology · Support"),
            U("INFE", "ITI · Infraestructura y equipos"), U("GA", "ITI · Gestión de activos"), U("TE", "ITI · Telecomunicaciones"),
        ]),
        new("FN", "Finance & Accountability",
        [
            U("FI", "Financial"), U("ETA", "External Audit"), U("FCOP", "Forecast / AOP"), U("ITC", "Internal Control"),
            U("CST", "Costeo"), U("CNT", "Contabilidad y nómina"),
        ]),
        new("HR", "Human Resources",
        [
            U("AMI", "Administración de personal"), U("RLR", "Relaciones laborales"), U("RCL", "Reclutamiento y selección"),
            U("CPA", "Capacitación"), U("POP", "Promoción interna"), U("GSG", "Gestión organizacional"),
        ]),
        new("EHS", "EHS", [U("ABI", "Ambiental"), U("SLO", "Salud ocupacional"), U("SGS", "Seguridad industrial"), U("ACE", "Accesos")]),
        new(AreaTransversal, "General / SGD",
        [
            U("MAN", "Manuales / Políticas"), U("MAP", "Mapa de procesos"), U("CD", "Control de Documentos"),
            U("AUD", "Auditorías Internas / Externas"), U("CAP", "Acciones correctivas y preventivas"),
            U("CHG", "Gestión de cambios"), U("TRN", "Capacitación y competencias"), U("REG", "Control de registros y retención"),
            U("RSK", "Riesgos y mejora continua"), U("NCM", "Contención y no conformidades"),
        ]),
    ];

    /// <summary>
    /// Las áreas del estándar. Cada una empieza con su unidad «General» (<see cref="ReglasCatalogo.UnidadGeneral"/>): ahí van los
    /// documentos de toda el área que no son de una subárea en particular, sin tener que elegir ninguna.
    /// </summary>
    public static readonly IReadOnlyList<AreaEstandar> Areas =
        [.. AreasDeCalidad.Select(a => a with { Unidades = [U(ReglasCatalogo.UnidadGeneral, ReglasCatalogo.NombreUnidadGeneral), .. a.Unidades] })];

    /// <summary>
    /// Tipos que no aplican en una unidad (la matriz de Calidad dice que el Plan de Inspección «no aplica» en Metrología).
    /// La unidad usa entonces sus propios tipos (todos menos estos) en lugar de heredar los del área.
    /// </summary>
    public static readonly IReadOnlyDictionary<(string Area, string Unidad), IReadOnlyList<string>> TiposNoAplicables =
        new Dictionary<(string, string), IReadOnlyList<string>> { [("QA", "MET")] = ["PI"] };

    /// <summary>Pasos de la plantilla de flujo por omisión de cada tipo: coincide con «Revisa» y «Aprueba» de la matriz de Calidad.</summary>
    public static readonly IReadOnlyList<(string Nombre, int Orden, bool EsAprobacion)> FlujoPorOmision =
        [("Revisión", 1, false), ("Aprobación", 2, true)];

    /// <summary>
    /// Comprueba que los datos cumplan las reglas del catálogo (abreviaturas válidas y sin repetir). Lo usan las pruebas y el
    /// cargador: un error aquí es un error de programación, no de usuario.
    /// </summary>
    public static IReadOnlyList<string> Validar()
    {
        var errores = new List<string>();

        void Codigo(string? valor, string que, int minimo)
        {
            try
            {
                if (ReglasCatalogo.NormalizarCodigo(valor, que, minimo) != valor)
                {
                    errores.Add($"{que} «{valor}» no está normalizado (mayúsculas, sin espacios).");
                }
            }
            catch (ReglaNegocioException ex)
            {
                errores.Add($"{que} «{valor}»: {ex.Message}");
            }
        }

        foreach (var g in Tipos.GroupBy(t => t.Codigo).Where(g => g.Count() > 1))
        {
            errores.Add($"El tipo {g.Key} está repetido.");
        }

        foreach (var t in Tipos)
        {
            Codigo(t.Codigo, "tipo", ReglasCatalogo.LongitudMinimaCodigoTipo);
        }

        foreach (var g in Areas.GroupBy(a => a.Codigo).Where(g => g.Count() > 1))
        {
            errores.Add($"El área {g.Key} está repetida.");
        }

        foreach (var a in Areas)
        {
            Codigo(a.Codigo, "área", ReglasCatalogo.LongitudMinimaCodigo);
            foreach (var g in a.Unidades.GroupBy(u => u.Codigo).Where(g => g.Count() > 1))
            {
                errores.Add($"La unidad {g.Key} está repetida en {a.Codigo}.");
            }

            foreach (var g in a.Unidades.GroupBy(u => u.Nombre).Where(g => g.Count() > 1))
            {
                errores.Add($"El nombre «{g.Key}» está repetido en {a.Codigo}.");
            }

            foreach (var u in a.Unidades)
            {
                Codigo(u.Codigo, "unidad", ReglasCatalogo.LongitudMinimaCodigo);
            }
        }

        foreach (var ((area, unidad), tipos) in TiposNoAplicables)
        {
            if (!Areas.Any(a => a.Codigo == area && a.Unidades.Any(u => u.Codigo == unidad)))
            {
                errores.Add($"La excepción {area}.{unidad} no existe en el catálogo.");
            }

            foreach (var t in tipos.Where(t => Tipos.All(x => x.Codigo != t)))
            {
                errores.Add($"La excepción {area}.{unidad} menciona el tipo {t}, que no existe.");
            }
        }

        return errores;
    }
}

/// <summary>Qué se creó al cargar el catálogo estándar (todo en cero = ya estaba cargado).</summary>
public sealed record ResultadoCargaCatalogo(int AreasNuevas, int SubAreasNuevas, int TiposNuevos, int AsociacionesNuevas, int PlantillasNuevas)
{
    public bool SinCambios => AreasNuevas + SubAreasNuevas + TiposNuevos + AsociacionesNuevas + PlantillasNuevas == 0;
}

public interface ICatalogoEstandarService
{
    /// <summary>
    /// Carga (o completa) el catálogo estándar de Calidad: áreas, subáreas con su abreviatura, tipos documentales, qué tipos
    /// aplican en cada área y una plantilla de flujo por omisión (Revisión → Aprobación) para los tipos que no tengan. Solo
    /// Administrador. Es <b>idempotente</b> y solo agrega: nunca modifica ni quita lo que ya existe (nombres, activación,
    /// dueños, asociaciones o plantillas que el Administrador haya ajustado).
    /// </summary>
    Task<ResultadoCargaCatalogo> CargarAsync(Actor actor, CancellationToken ct = default);
}
