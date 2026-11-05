namespace DocLibraryManagement.Domain;

public class Documento
{
    public int Id { get; set; }

    /// <summary>Código completo, p. ej. "PRO-CAL-012". No cambia con las revisiones.</summary>
    public required string Codigo { get; set; }

    public required string Titulo { get; set; }

    public int AreaId { get; set; }

    public Area? Area { get; set; }

    public int? SubAreaId { get; set; }

    public SubArea? SubArea { get; set; }

    public int TipoDocumentoId { get; set; }

    public TipoDocumento? TipoDocumento { get; set; }

    /// <summary>Consecutivo por (tipo, área). No se reutiliza, aunque el documento se retire.</summary>
    public int Consecutivo { get; set; }

    public EstadoDocumento Estado { get; set; } = EstadoDocumento.Activo;

    public Guid ResponsableId { get; set; }

    public Usuario? Responsable { get; set; }

    public DateTime FechaCreacionUtc { get; set; }

    public List<RevisionDocumento> Revisiones { get; set; } = [];

    /// <summary>Formato del código: TTT-AAA-NNN.</summary>
    public static string ConstruirCodigo(string codigoTipo, string codigoArea, int consecutivo) =>
        $"{codigoTipo}-{codigoArea}-{consecutivo:000}";
}

public class RevisionDocumento
{
    public int Id { get; set; }

    public int DocumentoId { get; set; }

    public Documento? Documento { get; set; }

    /// <summary>Número de revisión mayor (Rev.00, Rev.01…).</summary>
    public int Mayor { get; set; }

    /// <summary>Número de revisión menor (Rev.03.1). 0 si no es menor.</summary>
    public int Menor { get; set; }

    public EstadoRevision Estado { get; set; } = EstadoRevision.Borrador;

    public required string MotivoCambio { get; set; }

    public string? DescripcionCambio { get; set; }

    public DateTime? FechaAprobacionUtc { get; set; }

    public DateTime? FechaVigenciaUtc { get; set; }

    public DateTime? FechaProximaRevisionUtc { get; set; }

    public string? RutaArchivoFuente { get; set; }

    public string? HashArchivoFuente { get; set; }

    public string? RutaArchivoPdf { get; set; }

    public string? HashArchivoPdf { get; set; }

    /// <summary>Texto extraído del PDF para la búsqueda de texto completo (7.7).</summary>
    public string? TextoExtraido { get; set; }

    public DateTime FechaCreacionUtc { get; set; }

    public Guid AutorId { get; set; }

    public Usuario? Autor { get; set; }

    public string Etiqueta => Menor == 0 ? $"Rev.{Mayor:00}" : $"Rev.{Mayor:00}.{Menor}";
}
