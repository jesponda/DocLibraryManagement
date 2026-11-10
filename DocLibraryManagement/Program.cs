using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Correo;
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

// Si no se configura una carpeta, los archivos van a App_Data/archivos junto a la aplicación (fuera de wwwroot).
builder.Services.PostConfigure<ArchivosOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.RutaBase))
    {
        o.RutaBase = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "archivos");
    }
});

// El modo Carpeta (desarrollo) deja los correos como .eml en App_Data/correo, junto a la aplicación.
builder.Services.PostConfigure<CorreoOptions>(o =>
{
    if (string.IsNullOrWhiteSpace(o.CarpetaSalida))
    {
        o.CarpetaSalida = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "correo");
    }
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // Solo en desarrollo: en producción las migraciones se aplican con el script de despliegue.
    using var db = app.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    db.Database.Migrate();
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

app.MapEndpointsArchivos();
app.MapEndpointsListaMaestra();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Permite que las pruebas de integración usen WebApplicationFactory<Program>.
public partial class Program;
