using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Usuarios;
using Microsoft.AspNetCore.Components.Authorization;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Construye el <see cref="Actor"/> del usuario actual para pasarlo a los servicios.
/// La IP es de mejor esfuerzo: en Blazor Server el HttpContext solo existe en la solicitud inicial
/// del circuito, así que en acciones posteriores puede quedar vacía.
/// </summary>
/// <remarks>
/// En modo «Local» los roles y áreas se vuelven a leer de la base de datos en cada operación: un circuito de Blazor vive
/// horas, y quien fue desactivado o perdió un rol no debe seguir actuando con lo que tenía al abrir la pantalla.
/// </remarks>
public sealed class ProveedorActor(
    AuthenticationStateProvider autenticacion,
    IHttpContextAccessor http,
    IConfiguration configuracion,
    IServiceProvider servicios)
{
    private string? _ip;

    public async Task<Actor> ObtenerAsync()
    {
        var estado = await autenticacion.GetAuthenticationStateAsync();
        _ip ??= http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        var actor = Actor.Desde(estado.User, _ip);

        if (ConfiguracionSeguridad.ObtenerModo(configuracion) != ConfiguracionSeguridad.ModoLocal)
        {
            return actor;
        }

        var acceso = await servicios.GetRequiredService<IAutenticacionLocal>().ObtenerAccesoAsync(actor.Identificador);
        if (acceso is not { Activa: true })
        {
            throw new AccesoDenegadoException("Tu cuenta ya no está activa.");
        }

        return new Actor(acceso.Identificador, acceso.Roles, acceso.Areas, _ip);
    }
}
