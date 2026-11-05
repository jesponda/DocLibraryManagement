using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DocLibraryManagement.Tests.Datos;

/// <summary>
/// Los servicios reales conectados a la BD temporal de la fixture, con reloj controlable.
/// Cada instancia crea su propio contenedor, y cada prueba usa códigos únicos para no chocar con otras.
/// </summary>
public sealed class Escenario : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly IServiceScope _scope;

    public Escenario(BaseDatosFixture bd)
    {
        Bd = bd;
        Reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = bd.Conexion })
            .Build();

        var services = new ServiceCollection();
        services.AddInfrastructure(config);
        services.AddSingleton<TimeProvider>(Reloj);
        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateScope();
    }

    public BaseDatosFixture Bd { get; }

    public FakeTimeProvider Reloj { get; }

    public ICatalogoService Catalogo => _scope.ServiceProvider.GetRequiredService<ICatalogoService>();

    public IDocumentoService Documentos => _scope.ServiceProvider.GetRequiredService<IDocumentoService>();

    public IUsuarioSincronizador Usuarios => _scope.ServiceProvider.GetRequiredService<IUsuarioSincronizador>();

    public static string CodigoUnico(string prefijo = "") => (prefijo + Guid.NewGuid().ToString("N")).ToUpperInvariant()[..8];

    /// <summary>Crea al usuario en la BD (como lo haría el primer inicio de sesión) y devuelve su Actor.</summary>
    public async Task<Actor> NuevoActorAsync(IEnumerable<RolSistema> roles, params string[] areas)
    {
        var identificador = $"TEST\\{Guid.NewGuid():N}";
        var rolesSet = roles.Append(RolSistema.Lector).ToHashSet();
        var areasSet = areas.ToHashSet(StringComparer.OrdinalIgnoreCase);
        await Usuarios.SincronizarAsync(identificador, $"Usuario {identificador[5..11]}", null, rolesSet.ToList(), areasSet.ToList());
        return new Actor(identificador, rolesSet, areasSet);
    }

    public Task<Actor> NuevoAdminAsync() => NuevoActorAsync([RolSistema.Administrador]);

    public Task<Actor> NuevoAutorAsync(params string[] areas) => NuevoActorAsync([RolSistema.Autor], areas);

    /// <summary>Área activa con un tipo asociado, listos para crear documentos.</summary>
    public async Task<(int AreaId, string CodigoArea, int TipoId, string CodigoTipo)> NuevaAreaConTipoAsync(Actor admin)
    {
        var codigoArea = CodigoUnico("A");
        var codigoTipo = CodigoUnico("T");
        var areaId = await Catalogo.CrearAreaAsync(admin, codigoArea, $"Área {codigoArea}");
        var tipoId = await Catalogo.CrearTipoAsync(admin, codigoTipo, $"Tipo {codigoTipo}", 12);
        await Catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [tipoId]);
        return (areaId, codigoArea, tipoId, codigoTipo);
    }

    public AppDbContext Db() => Bd.Crear();

    public async Task<List<EventoAuditoria>> AuditoriaDeAsync(string entidadId)
    {
        await using var db = Db();
        return await db.Auditoria.Where(e => e.EntidadId == entidadId).OrderBy(e => e.Id).ToListAsync();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _sp.Dispose();
    }
}
