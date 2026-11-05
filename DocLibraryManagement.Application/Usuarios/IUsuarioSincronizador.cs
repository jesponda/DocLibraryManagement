using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Usuarios;

public interface IUsuarioSincronizador
{
    /// <summary>
    /// Crea el usuario en el primer acceso o actualiza sus datos y último acceso.
    /// Si se indican roles y áreas, se guarda una copia informativa (ver <see cref="Usuario.RolesUltimoAcceso"/>).
    /// </summary>
    Task<Usuario> SincronizarAsync(
        string identificador,
        string nombreVisible,
        string? correo,
        IReadOnlyCollection<RolSistema>? roles = null,
        IReadOnlyCollection<string>? areas = null,
        CancellationToken cancellationToken = default);
}
