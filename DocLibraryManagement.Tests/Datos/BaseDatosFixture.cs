using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Datos;

/// <summary>
/// Crea una base de datos LocalDB temporal con las migraciones reales y la elimina al terminar.
/// Se usa SQL Server (no InMemory) porque las garantías que se prueban son índices filtrados únicos.
/// </summary>
public sealed class BaseDatosFixture : IAsyncLifetime
{
    private readonly string _nombre = $"DocLibrary_Test_{Guid.NewGuid():N}";

    public string Conexion => $"Server=(localdb)\\mssqllocaldb;Database={_nombre};Trusted_Connection=True";

    public AppDbContext Crear() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(Conexion).Options);

    public async Task InitializeAsync()
    {
        await using var db = Crear();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = Crear();
        await db.Database.EnsureDeletedAsync();
    }

    /// <summary>Crea un documento con su área, tipo y responsable propios (códigos únicos por llamada).</summary>
    public static async Task<Documento> CrearDocumentoAsync(AppDbContext db)
    {
        var sufijo = Guid.NewGuid().ToString("N")[..8];

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Identificador = $"TEST\\{sufijo}",
            NombreVisible = "Usuario de prueba",
            FechaCreacionUtc = DateTime.UtcNow,
            UltimoAccesoUtc = DateTime.UtcNow,
        };
        var area = new Area { Codigo = sufijo, Nombre = $"Área {sufijo}" };
        var tipo = new TipoDocumento { Codigo = sufijo, Nombre = $"Tipo {sufijo}" };
        var documento = new Documento
        {
            Codigo = Documento.ConstruirCodigo(tipo.Codigo, area.Codigo, 1),
            Titulo = "Procedimiento de prueba",
            Area = area,
            TipoDocumento = tipo,
            Consecutivo = 1,
            Responsable = usuario,
            FechaCreacionUtc = DateTime.UtcNow,
        };

        db.Documentos.Add(documento);
        await db.SaveChangesAsync();
        return documento;
    }

    public static RevisionDocumento NuevaRevision(Documento documento, int mayor, EstadoRevision estado, int menor = 0) =>
        new()
        {
            DocumentoId = documento.Id,
            Mayor = mayor,
            Menor = menor,
            Estado = estado,
            MotivoCambio = "Motivo de prueba",
            AutorId = documento.ResponsableId,
            FechaCreacionUtc = DateTime.UtcNow,
        };
}
