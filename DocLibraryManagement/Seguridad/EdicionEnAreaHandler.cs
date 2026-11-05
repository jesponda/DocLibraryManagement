using DocLibraryManagement.Application.Seguridad;
using Microsoft.AspNetCore.Authorization;

namespace DocLibraryManagement.Seguridad;

/// <summary>
/// Autorización por recurso: el recurso es el código del área del documento.
/// Uso: <c>await auth.AuthorizeAsync(user, "CAL", new EdicionEnAreaRequirement())</c>.
/// </summary>
public sealed class EdicionEnAreaRequirement : IAuthorizationRequirement;

public sealed class EdicionEnAreaHandler : AuthorizationHandler<EdicionEnAreaRequirement, string>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        EdicionEnAreaRequirement requirement,
        string codigoArea)
    {
        if (context.User.PuedeEditarEnArea(codigoArea))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
