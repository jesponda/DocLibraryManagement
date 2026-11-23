using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Application.Usuarios;

/// <summary>Una cuenta de la lista de usuarios (modo de autenticación «Local»).</summary>
public sealed record CuentaDto(
    Guid Id,
    string Identificador,
    string Nombre,
    string? Correo,
    bool Activa,
    IReadOnlyList<RolSistema> Roles,
    IReadOnlyList<string> Areas,
    bool TieneContrasena,
    bool DebeCambiarContrasena,
    DateTime? BloqueadaHastaUtc,
    DateTime UltimoAccesoUtc);

/// <param name="Roles">Sin «Lector»: todas las personas lo tienen.</param>
/// <param name="Areas">Códigos de área donde puede editar (obligatorio con el rol Autor).</param>
public sealed record DatosCuenta(
    string Nombre,
    string? Correo,
    IReadOnlyCollection<RolSistema> Roles,
    IReadOnlyCollection<string> Areas);

/// <summary>La contraseña temporal se muestra una sola vez: no se puede volver a consultar.</summary>
public sealed record ResultadoAlta(Guid Id, string Identificador, string ContrasenaTemporal);

/// <summary>Gestión de la lista de usuarios y sus roles. Todo es solo para el Administrador.</summary>
public interface ICuentasService
{
    Task<IReadOnlyList<CuentaDto>> ListarAsync(Actor actor, CancellationToken ct = default);

    /// <summary>Da de alta a una persona con una contraseña temporal que deberá cambiar en su primer acceso.</summary>
    Task<ResultadoAlta> CrearAsync(Actor actor, string identificador, DatosCuenta datos, CancellationToken ct = default);

    /// <summary>Cambia nombre, correo, roles y áreas. El identificador no cambia nunca.</summary>
    Task ActualizarAsync(Actor actor, Guid id, DatosCuenta datos, CancellationToken ct = default);

    /// <summary>Genera otra contraseña temporal, desbloquea la cuenta y cierra sus sesiones abiertas.</summary>
    Task<string> RestablecerContrasenaAsync(Actor actor, Guid id, CancellationToken ct = default);

    /// <summary>Desactivar cierra las sesiones y evita que vuelva a entrar; no borra nada (la bitácora conserva su historia).</summary>
    Task CambiarEstadoAsync(Actor actor, Guid id, bool activa, CancellationToken ct = default);
}

public enum ResultadoInicioSesion
{
    Correcto,

    /// <summary>Usuario o contraseña incorrectos, cuenta inactiva o sin contraseña: no se distingue a propósito.</summary>
    Rechazado,

    Bloqueado,
}

/// <summary>Lo vigente de una cuenta, leído de la base de datos (no de la cookie).</summary>
public sealed record AccesoCuenta(
    Guid Id,
    string Identificador,
    string Nombre,
    bool Activa,
    bool DebeCambiarContrasena,
    Guid Sello,
    IReadOnlySet<RolSistema> Roles,
    IReadOnlySet<string> Areas);

public sealed record RespuestaInicioSesion(ResultadoInicioSesion Resultado, AccesoCuenta? Cuenta = null);

/// <summary>Inicio de sesión con usuario y contraseña propios. No requiere actor: lo usa quien aún no entró.</summary>
public interface IAutenticacionLocal
{
    Task<RespuestaInicioSesion> IniciarSesionAsync(string identificador, string contrasena, string? direccionIp, CancellationToken ct = default);

    /// <summary>El estado vigente de la cuenta (para validar la cookie y construir al actor). Nulo si no existe.</summary>
    Task<AccesoCuenta?> ObtenerAccesoAsync(string identificador, CancellationToken ct = default);

    /// <summary>Quien ya tiene sesión cambia su propia contraseña (pide la actual). Devuelve el acceso con el sello nuevo.</summary>
    Task<AccesoCuenta> CambiarContrasenaAsync(string identificador, string actual, string nueva, string? direccionIp, CancellationToken ct = default);

    /// <summary>
    /// Si no existe ninguna cuenta con contraseña, crea el Administrador inicial. Devuelve la contraseña temporal
    /// (la dada o una generada) o <c>null</c> si no hizo falta crear nada.
    /// </summary>
    Task<string?> AsegurarAdministradorInicialAsync(string identificador, string nombre, string? contrasenaTemporal, CancellationToken ct = default);
}

public static class ReglasContrasena
{
    public const int LongitudMinima = 10;

    /// <summary>Mensaje en español si la contraseña no cumple; <c>null</c> si es aceptable.</summary>
    public static string? Validar(string? contrasena, string identificador)
    {
        if (string.IsNullOrEmpty(contrasena) || contrasena.Length < LongitudMinima)
        {
            return $"La contraseña debe tener al menos {LongitudMinima} caracteres.";
        }

        if (contrasena.Length > 128)
        {
            return "La contraseña no puede tener más de 128 caracteres.";
        }

        if (contrasena.Trim().Length == 0 || contrasena.Distinct().Count() < 4)
        {
            return "La contraseña es demasiado simple: usa letras, números o símbolos variados.";
        }

        if (contrasena.Contains(identificador, StringComparison.OrdinalIgnoreCase))
        {
            return "La contraseña no puede contener tu nombre de usuario.";
        }

        return null;
    }
}
