using DocLibraryManagement.Application.Comun;
using Microsoft.AspNetCore.Components.Authorization;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Construye el <see cref="Actor"/> del usuario actual para pasarlo a los servicios.
/// La IP es de mejor esfuerzo: en Blazor Server el HttpContext solo existe en la solicitud inicial
/// del circuito, así que en acciones posteriores puede quedar vacía.
/// </summary>
public sealed class ProveedorActor(AuthenticationStateProvider autenticacion, IHttpContextAccessor http)
{
    private string? _ip;

    public async Task<Actor> ObtenerAsync()
    {
        var estado = await autenticacion.GetAuthenticationStateAsync();
        _ip ??= http.HttpContext?.Connection.RemoteIpAddress?.ToString();
        return Actor.Desde(estado.User, _ip);
    }
}
