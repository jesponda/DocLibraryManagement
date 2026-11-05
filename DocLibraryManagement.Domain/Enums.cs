namespace DocLibraryManagement.Domain;

/// <summary>Roles del sistema (sección 5 del prompt de desarrollo).</summary>
public enum RolSistema
{
    Lector = 1,
    Autor = 2,
    Revisor = 3,
    Aprobador = 4,
    Auditor = 5,
    Administrador = 6,
}

/// <summary>Estado de una revisión (sección 6.2).</summary>
public enum EstadoRevision
{
    Borrador = 1,
    EnRevision = 2,
    EnAprobacion = 3,
    Aprobado = 4,
    Vigente = 5,
    Obsoleto = 6,
}

/// <summary>Estado global del documento.</summary>
public enum EstadoDocumento
{
    Activo = 1,
    Retirado = 2,
}

public static class EstadoRevisionExtensions
{
    /// <summary>
    /// Una revisión "abierta" es la que está en trabajo o esperando vigencia.
    /// Solo puede existir una por documento (regla 7.1.5).
    /// </summary>
    public static bool EsAbierta(this EstadoRevision estado) =>
        estado is EstadoRevision.Borrador
            or EstadoRevision.EnRevision
            or EstadoRevision.EnAprobacion
            or EstadoRevision.Aprobado;
}
