using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Components.Shared;

/// <summary>Textos en español de los estados, en un solo lugar para que todas las pantallas coincidan.</summary>
public static class Presentacion
{
    public static string NombreEstado(EstadoRevision estado) => estado switch
    {
        EstadoRevision.Borrador => "Borrador",
        EstadoRevision.EnRevision => "En revisión",
        EstadoRevision.EnAprobacion => "En aprobación",
        EstadoRevision.Aprobado => "Aprobado",
        EstadoRevision.Vigente => "Vigente",
        EstadoRevision.Obsoleto => "Obsoleto",
        EstadoRevision.Cancelado => "Cancelado",
        _ => estado.ToString(),
    };

    public static string NombreEstado(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Activo => "Activo",
        EstadoDocumento.Retirado => "Retirado",
        _ => estado.ToString(),
    };

    /// <summary>Color de la insignia según qué tan cerca está la revisión periódica.</summary>
    public static string ClaseVencimiento(EstadoVencimiento estado) => estado switch
    {
        EstadoVencimiento.Vencido => "text-bg-danger",
        EstadoVencimiento.PorVencer7 => "text-bg-warning",
        EstadoVencimiento.PorVencer30 => "text-bg-info",
        _ => "text-bg-success",
    };

    /// <summary>«faltan 5 día(s)», «venció hoy» o «venció hace 3 día(s)» a partir de los días con signo.</summary>
    public static string TextoDias(int dias) => dias switch
    {
        > 0 => $"faltan {dias} día(s)",
        0 => "venció hoy",
        _ => $"venció hace {-dias} día(s)",
    };

    public static string NombreEstado(EstadoSolicitudSinCambios estado) => ReglasReporte.NombreSolicitud(estado);
}
