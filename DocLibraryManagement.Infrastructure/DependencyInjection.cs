using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Infrastructure.Archivos;
using DocLibraryManagement.Infrastructure.Catalogo;
using DocLibraryManagement.Infrastructure.Consulta;
using DocLibraryManagement.Infrastructure.Correo;
using DocLibraryManagement.Infrastructure.Documentos;
using DocLibraryManagement.Infrastructure.Flujo;
using DocLibraryManagement.Infrastructure.Persistencia;
using DocLibraryManagement.Infrastructure.Reportes;
using DocLibraryManagement.Infrastructure.Vencimientos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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

        services.Configure<ArchivosOptions>(configuration.GetSection(ArchivosOptions.Seccion));
        services.AddSingleton<IFileStore, FileStoreEnDisco>();

        services.AddScoped<IUsuarioSincronizador, UsuarioSincronizador>();
        services.AddScoped<ICatalogoService, CatalogoService>();
        services.AddScoped<ICatalogoEstandarService, CatalogoEstandarService>();
        services.Configure<DocumentosOptions>(configuration.GetSection(DocumentosOptions.Seccion));
        services.AddScoped<IDocumentoService, DocumentoService>();
        services.AddScoped<IRevisionService, RevisionService>();
        services.AddScoped<IFlujoService, FlujoService>();
        services.AddScoped<IPublicacionService, PublicacionService>();
        services.AddScoped<IVencimientosService, VencimientosService>();

        services.AddScoped<IReportesService, ReportesService>();
        services.AddScoped<IAuditoriaService, AuditoriaService>();
        services.AddSingleton<IExportadorTablas, ExportadorTablas>();

        services.AddSingleton<IExtractorTexto, ExtractorTextoPdf>();
        services.AddSingleton<IExportadorListaMaestra, ExportadorListaMaestra>();
        services.AddScoped<IConsultaService, ConsultaService>();
        services.AddScoped<IIndexadorService, IndexadorService>();

        services.Configure<TareasDeFondoOptions>(configuration.GetSection(TareasDeFondoOptions.Seccion));
        services.AddHostedService<TareasDeFondo>();

        // Correo (7.8): cola en BD + envío por SMTP, o a una carpeta en desarrollo.
        services.Configure<CorreoOptions>(configuration.GetSection(CorreoOptions.Seccion));
        services.AddSingleton<IEncoladorNotificaciones, EncoladorNotificaciones>();
        services.AddSingleton<ICorreoSender>(sp =>
            sp.GetRequiredService<IOptions<CorreoOptions>>().Value.Modo == ModoCorreo.Smtp
                ? ActivatorUtilities.CreateInstance<SmtpCorreoSender>(sp)
                : ActivatorUtilities.CreateInstance<CarpetaCorreoSender>(sp));
        services.AddScoped<IColaCorreoService, ColaCorreoService>();
        services.AddScoped<IUsuariosService, UsuariosService>();
        services.AddHostedService<EnvioDeCorreo>();

        return services;
    }
}
