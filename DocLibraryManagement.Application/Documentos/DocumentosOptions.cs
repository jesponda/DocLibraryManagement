namespace DocLibraryManagement.Application.Documentos;

/// <summary>Opciones de la sección "Documentos" de appsettings.</summary>
public class DocumentosOptions
{
    public const string Seccion = "Documentos";

    /// <summary>
    /// Código del sitio, primer segmento del código de los documentos (<c>QRT-TIPO-ÁREA-UNIDAD-NNN</c>). Por omisión «QRT»
    /// (Allegion Querétaro). Vacío = sin prefijo de sitio. Cambiarlo solo afecta a los documentos nuevos.
    /// </summary>
    public string CodigoSitio { get; set; } = "QRT";
}
