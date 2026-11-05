using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Infrastructure.Catalogo;
using DocLibraryManagement.Infrastructure.Documentos;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DocLibraryManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        // En Blazor Server un circuito vive minutos u horas: un DbContext por circuito acumularía estado
        // y fallaría con operaciones concurrentes. Cada operación crea y libera el suyo desde la fábrica.
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlServer(connectionString));
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<IUsuarioSincronizador, UsuarioSincronizador>();
        services.AddScoped<ICatalogoService, CatalogoService>();
        services.AddScoped<IDocumentoService, DocumentoService>();

        return services;
    }
}
