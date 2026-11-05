using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocLibraryManagement.Infrastructure.Persistencia;

/// <summary>
/// Solo para herramientas de diseño (dotnet ef). Usa la variable DOCLIB_CONNECTION si existe;
/// si no, LocalDB. Nunca se usa en ejecución.
/// </summary>
internal sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("DOCLIB_CONNECTION")
            ?? "Server=(localdb)\\mssqllocaldb;Database=DocLibraryManagement-Design;Trusted_Connection=True";

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options);
    }
}
