using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Usuarios;
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

var modoAutenticacion = ConfiguracionSeguridad.ObtenerModo(app.Configuration);

if (modoAutenticacion == ConfiguracionSeguridad.ModoLocal)
{
    // Primer arranque sin cuentas: se crea el Administrador inicial para poder entrar y dar de alta a los demás.
    var inicial = app.Configuration.GetSection("Autenticacion:AdministradorInicial");
    using var alcance = app.Services.CreateScope();
    var temporal = await alcance.ServiceProvider.GetRequiredService<IAutenticacionLocal>().AsegurarAdministradorInicialAsync(
        inicial["Usuario"] ?? "admin", inicial["Nombre"] ?? "Administrador", inicial["ContrasenaTemporal"]);
    if (temporal is not null)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Cuentas");
        log.LogWarning(
            "Se creó el Administrador inicial «{Usuario}». Contraseña temporal (se muestra solo ahora y se debe cambiar al entrar): {Contrasena}",
            inicial["Usuario"] ?? "admin", temporal);
    }
}

app.UseAuthentication();
app.UseAuthorization();
if (modoAutenticacion == ConfiguracionSeguridad.ModoLocal)
{
    app.UseCambioDeContrasenaObligatorio();
}

app.UseAntiforgery();

// Los archivos del front (CSS, scripts, logo) son públicos: sin esto, las pantallas que se ven antes de iniciar sesión
// (el acceso de desarrollo, «acceso denegado») salían sin estilo. Los documentos NO están aquí: viven fuera de wwwroot y
// solo se entregan por /archivos, que sí exige sesión y permiso.
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

if (modoAutenticacion == ConfiguracionSeguridad.ModoDesarrollo)
{
    app.MapEndpointsDesarrollo();
}

if (modoAutenticacion == ConfiguracionSeguridad.ModoLocal)
{
    app.MapEndpointsCuentas();
}

app.MapEndpointsArchivos();
app.MapEndpointsListaMaestra();
app.MapEndpointsReportes();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

// Permite que las pruebas de integración usen WebApplicationFactory<Program>.
public partial class Program;
