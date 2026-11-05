namespace DocLibraryManagement.Domain;

/// <summary>
/// Usuario corporativo sincronizado en el primer inicio de sesión.
/// Los roles y las áreas NO se guardan aquí: vienen de los grupos de AD (sección 5).
/// </summary>
public class Usuario
{
    public Guid Id { get; set; }

    /// <summary>Identificador corporativo estable (p. ej. "DOMINIO\usuario" o UPN).</summary>
    public required string Identificador { get; set; }

    public required string NombreVisible { get; set; }

    public string? Correo { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime FechaCreacionUtc { get; set; }

    public DateTime UltimoAccesoUtc { get; set; }

    /// <summary>
    /// Copia de los roles que el usuario tenía en su último acceso (separados por coma).
    /// Es solo informativa: sirve para ofrecer candidatos al asignar pasos de un flujo.
    /// La autorización real siempre sale de los grupos de AD vigentes, nunca de esta copia.
    /// </summary>
    public string RolesUltimoAcceso { get; set; } = "";

    /// <summary>Códigos de área en los que el usuario podía editar en su último acceso (separados por coma).</summary>
    public string AreasUltimoAcceso { get; set; } = "";

    public bool TuvoRolEnUltimoAcceso(RolSistema rol) =>
        RolesUltimoAcceso.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(rol.ToString(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Bitácora inmutable (7.6). Solo inserción: el DbContext rechaza modificarla o borrarla.
/// </summary>
public class EventoAuditoria
{
    public long Id { get; set; }

    public DateTime FechaUtc { get; set; }

    /// <summary>Identificador corporativo de quien ejecutó la acción.</summary>
    public required string Usuario { get; set; }

    public required string Accion { get; set; }

    public required string Entidad { get; set; }

    public string? EntidadId { get; set; }

    public string? ValorAnterior { get; set; }

    public string? ValorNuevo { get; set; }

    public string? Comentario { get; set; }

    public string? DireccionIp { get; set; }
}
