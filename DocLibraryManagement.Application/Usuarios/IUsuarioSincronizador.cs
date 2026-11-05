using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Usuarios;

public interface IUsuarioSincronizador
{
    /// <summary>Crea el usuario en el primer acceso o actualiza sus datos y último acceso.</summary>
    Task<Usuario> SincronizarAsync(
        string identificador,
        string nombreVisible,
        string? correo,
        CancellationToken cancellationToken = default);
}
