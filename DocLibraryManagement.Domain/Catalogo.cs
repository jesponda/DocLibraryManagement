namespace DocLibraryManagement.Domain;

/// <summary>Área de la planta. Su código forma parte del código del documento.</summary>
public class Area
{
    public int Id { get; set; }

    /// <summary>Abreviatura usada en el código del documento (p. ej. "CAL").</summary>
    public required string Codigo { get; set; }

    public required string Nombre { get; set; }

    public bool Activa { get; set; } = true;

    /// <summary>
    /// Dueño del área: persona con rol Aprobador que aprueba las revisiones menores (correcciones
    /// editoriales) de los documentos del área. Opcional; sin dueño no se pueden enviar revisiones menores.
    /// </summary>
    public Guid? DuenoId { get; set; }

    public Usuario? Dueno { get; set; }

    public List<SubArea> SubAreas { get; set; } = [];

    public List<AreaTipoDocumento> TiposDocumento { get; set; } = [];
}

/// <summary>Subárea (segundo y último nivel). Es una clasificación: no forma parte del código.</summary>
public class SubArea
{
    public int Id { get; set; }

    public int AreaId { get; set; }

    public Area? Area { get; set; }

    public required string Nombre { get; set; }

    public bool Activa { get; set; } = true;

    public List<SubAreaTipoDocumento> TiposDocumento { get; set; } = [];
}

public class TipoDocumento
{
    public int Id { get; set; }

    /// <summary>Abreviatura usada en el código del documento (p. ej. "PRO").</summary>
    public required string Codigo { get; set; }

    public required string Nombre { get; set; }

    /// <summary>Periodo de revisión periódica (7.4). Por defecto 12 meses.</summary>
    public int MesesRevision { get; set; } = 12;

    public bool Activo { get; set; } = true;
}

/// <summary>Tipos de documento que se pueden crear en un área.</summary>
public class AreaTipoDocumento
{
    public int AreaId { get; set; }

    public Area? Area { get; set; }

    public int TipoDocumentoId { get; set; }

    public TipoDocumento? TipoDocumento { get; set; }
}

/// <summary>Tipos de documento propios de una subárea. Si no tiene ninguno, hereda los del área.</summary>
public class SubAreaTipoDocumento
{
    public int SubAreaId { get; set; }

    public SubArea? SubArea { get; set; }

    public int TipoDocumentoId { get; set; }

    public TipoDocumento? TipoDocumento { get; set; }
}
