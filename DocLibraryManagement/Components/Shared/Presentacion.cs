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
        _ => estado.ToString(),
    };

    public static string NombreEstado(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Activo => "Activo",
        EstadoDocumento.Retirado => "Retirado",
        _ => estado.ToString(),
    };
}
