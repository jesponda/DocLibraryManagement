using DocLibraryManagement.Components;
using DocLibraryManagement.Infrastructure;
using DocLibraryManagement.Infrastructure.Persistencia;
using DocLibraryManagement.Seguridad;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSeguridadDocumental(builder.Configuration, builder.Environment);
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Solo en desarrollo: en producción las migraciones se aplican con el script de despliegue.
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // El valor por defecto de HSTS es 30 días; ajustar para producción (https://aka.ms/aspnetcore-hsts).
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();

if (ConfiguracionSeguridad.ObtenerModo(app.Configuration) == ConfiguracionSeguridad.ModoDesarrollo)
{
    app.MapEndpointsDesarrollo();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Permite que las pruebas de integración usen WebApplicationFactory<Program>.
public partial class Program;
