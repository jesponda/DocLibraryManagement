using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Archivos;

public enum TipoArchivo
{
    /// <summary>PDF publicado: es lo que ven los lectores. Obligatorio para enviar a revisión.</summary>
    Pdf = 1,

    /// <summary>Fuente editable (Word/Excel…). Opcional; solo la ven Autor del área, Administrador y Auditor.</summary>
    Fuente = 2,
}

public sealed record ArchivoGuardado(string Ruta, string HashSha256, long Bytes);

public sealed record ArchivoParaDescarga(Stream Contenido, string NombreDescarga, string TipoContenido, bool EnLinea);

/// <summary>Opciones enlazadas desde la sección "Archivos" de appsettings.</summary>
public class ArchivosOptions
{
    public const string Seccion = "Archivos";

    /// <summary>Carpeta raíz del almacén. Debe estar FUERA de la raíz web. Si está vacía, la web usa App_Data/archivos.</summary>
    public string RutaBase { get; set; } = "";

    public int TamanoMaximoMb { get; set; } = 50;

    public long TamanoMaximoBytes => TamanoMaximoMb * 1024L * 1024L;
}

public interface IFileStore
{
    /// <summary>
    /// Guarda el contenido con un nombre generado (nunca el original), calcula su SHA-256 y lo
    /// escribe de forma atómica. <paramref name="validarCabecera"/> recibe los primeros bytes antes de
    /// escribir nada definitivo y debe lanzar <see cref="ReglaNegocioException"/> si no son válidos.
    /// </summary>
    Task<ArchivoGuardado> GuardarAsync(
        Stream contenido,
        string extension,
        long maximoBytes,
        Action<byte[]> validarCabecera,
        CancellationToken ct = default);

    Stream Abrir(string ruta);

    Task EliminarAsync(string ruta, CancellationToken ct = default);
}

/// <summary>Reglas puras de qué archivos se aceptan. No dependen del disco, así que se prueban sin él.</summary>
public static class ReglasArchivo
{
    public const int BytesDeCabecera = 16;

    private static readonly string[] ExtensionesFuente =
        [".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pptm", ".odt", ".ods", ".odp"];

    private static readonly byte[] FirmaPdf = "%PDF-"u8.ToArray();
    private static readonly byte[] FirmaZip = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] FirmaOle = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Devuelve la extensión normalizada (minúsculas) o rechaza el archivo.</summary>
    public static string ValidarExtension(TipoArchivo tipo, string? nombreOriginal)
    {
        var extension = Path.GetExtension(nombreOriginal ?? "").ToLowerInvariant();

        if (tipo == TipoArchivo.Pdf)
        {
            return extension == ".pdf"
                ? extension
                : throw new ReglaNegocioException("El archivo publicado debe ser un PDF (.pdf).");
        }

        return ExtensionesFuente.Contains(extension)
            ? extension
            : throw new ReglaNegocioException(
                $"La fuente editable debe ser un archivo de Word, Excel, PowerPoint o equivalente ({string.Join(", ", ExtensionesFuente)}).");
    }

    /// <summary>
    /// Comprueba que el contenido real corresponde a la extensión. Un ejecutable renombrado a .pdf
    /// pasa la extensión pero no la firma.
    /// </summary>
    public static void ValidarCabecera(string extension, byte[] cabecera)
    {
        var valido = extension switch
        {
            ".pdf" => Empieza(cabecera, FirmaPdf),
            ".doc" or ".xls" or ".ppt" => Empieza(cabecera, FirmaOle),
            ".docx" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" or ".odt" or ".ods" or ".odp" => Empieza(cabecera, FirmaZip),
            _ => false,
        };

        if (!valido)
        {
            throw new ReglaNegocioException(
                "El contenido del archivo no corresponde a su extensión. Verifica que no esté dañado ni renombrado.");
        }
    }

    public static string TipoContenido(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xlsm" => "application/vnd.ms-excel.sheet.macroEnabled.12",
        ".ppt" => "application/vnd.ms-powerpoint",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".pptm" => "application/vnd.ms-powerpoint.presentation.macroEnabled.12",
        ".odt" => "application/vnd.oasis.opendocument.text",
        ".ods" => "application/vnd.oasis.opendocument.spreadsheet",
        ".odp" => "application/vnd.oasis.opendocument.presentation",
        _ => "application/octet-stream",
    };

    private static bool Empieza(byte[] datos, byte[] firma) =>
        datos.Length >= firma.Length && datos.AsSpan(0, firma.Length).SequenceEqual(firma);
}
