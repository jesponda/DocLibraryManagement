namespace DocLibraryManagement.Application.Consulta;

/// <summary>Resultado de leer el texto de un PDF.</summary>
/// <param name="Texto">Texto extraído, con espacios normalizados. Vacío si el PDF no tiene texto seleccionable.</param>
/// <param name="PudoLeerse">false si el archivo no se pudo interpretar (dañado, protegido…).</param>
public sealed record ExtraccionTexto(string Texto, bool PudoLeerse)
{
    public bool TieneTexto => PudoLeerse && Texto.Length > 0;
}

public interface IExtractorTexto
{
    /// <summary>Lee el texto de un PDF. Nunca lanza por un archivo dañado: devuelve <c>PudoLeerse = false</c>.</summary>
    Task<ExtraccionTexto> ExtraerAsync(Stream pdf, CancellationToken ct = default);
}

public interface IExportadorListaMaestra
{
    /// <summary>CSV en UTF-8 con BOM (para que Excel respete los acentos), separado por comas.</summary>
    byte[] ACsv(IReadOnlyList<FilaListaMaestra> filas);

    byte[] AXlsx(IReadOnlyList<FilaListaMaestra> filas);
}

public interface IIndexadorService
{
    /// <summary>
    /// Extrae el texto de las revisiones vigentes u obsoletas cuyo PDF aún no se ha leído (p. ej. las publicadas
    /// antes de existir la búsqueda). Devuelve cuántas procesó. Un PDF sin texto se marca como ya leído para no
    /// reintentarlo cada vez.
    /// </summary>
    Task<int> IndexarPendientesAsync(int maximo = 25, CancellationToken ct = default);
}
