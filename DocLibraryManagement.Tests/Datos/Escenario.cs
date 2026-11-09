using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
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

    public Escenario(BaseDatosFixture bd, int tamanoMaximoMb = 50)
    {
        Bd = bd;
        Reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
        RutaArchivos = Path.Combine(Path.GetTempPath(), "doclib-tests", Guid.NewGuid().ToString("N"));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = bd.Conexion,
                ["Archivos:RutaBase"] = RutaArchivos,
                ["Archivos:TamanoMaximoMb"] = tamanoMaximoMb.ToString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(); // el host real lo trae; este contenedor mínimo no
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

    public IRevisionService Revisiones => _scope.ServiceProvider.GetRequiredService<IRevisionService>();

    public IFlujoService Flujo => _scope.ServiceProvider.GetRequiredService<IFlujoService>();

    public string RutaArchivos { get; }

    /// <summary>Proveedor del ámbito del escenario, para resolver servicios que no tienen atajo propio.</summary>
    public IServiceProvider Scope() => _scope.ServiceProvider;

    /// <summary>PDF mínimo pero con la firma correcta. Cada llamada puede variar el contenido con <paramref name="marca"/>.</summary>
    public static byte[] PdfDePrueba(string marca = "base") =>
        System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4\n% prueba {marca}\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");

    public async Task<Guid> IdDeAsync(Actor actor)
    {
        await using var db = Db();
        return (await db.Usuarios.SingleAsync(u => u.Identificador == actor.Identificador)).Id;
    }

    public Task<Actor> NuevoRevisorAsync() => NuevoActorAsync([RolSistema.Revisor]);

    public Task<Actor> NuevoAprobadorAsync() => NuevoActorAsync([RolSistema.Aprobador]);

    public async Task SubirPdfAsync(Actor actor, int revisionId, string marca = "base", string nombre = "documento.pdf")
    {
        using var s = new MemoryStream(PdfDePrueba(marca));
        await Revisiones.SubirArchivoAsync(actor, revisionId, TipoArchivo.Pdf, nombre, s);
    }

    /// <summary>
    /// Todo listo para probar el flujo: área con tipo, plantilla Revisión → Aprobación, autor con su
    /// documento (Rev.00 en borrador) y PDF subido, más un revisor y un aprobador.
    /// </summary>
    public async Task<EscenarioFlujo> PrepararFlujoAsync(bool conPdf = true, bool conPlantilla = true)
    {
        var admin = await NuevoAdminAsync();
        var (areaId, codigoArea, tipoId, _) = await NuevaAreaConTipoAsync(admin);
        if (conPlantilla)
        {
            await Flujo.GuardarPlantillaAsync(admin, tipoId,
            [
                new PasoPlantillaDto(1, TipoPaso.Revision, "Revisión técnica"),
                new PasoPlantillaDto(2, TipoPaso.Aprobacion, "Aprobación"),
            ]);
        }

        var autor = await NuevoAutorAsync(codigoArea);
        var revisor = await NuevoRevisorAsync();
        var aprobador = await NuevoAprobadorAsync();
        var doc = await Documentos.CrearAsync(autor, new NuevoDocumento("Procedimiento de prueba", areaId, null, tipoId));
        var revisionId = doc.Revisiones.Single().Id;
        if (conPdf)
        {
            await SubirPdfAsync(autor, revisionId);
        }

        return new EscenarioFlujo(admin, autor, revisor, aprobador, areaId, codigoArea, tipoId, doc, revisionId,
            await IdDeAsync(revisor), await IdDeAsync(aprobador));
    }

    /// <summary>Envía la revisión con el revisor y el aprobador del escenario.</summary>
    public Task IniciarAsync(EscenarioFlujo f) =>
        Flujo.IniciarAsync(f.Autor, f.RevisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]);

    public async Task<InstanciaFlujoDto> UltimoFlujoAsync(EscenarioFlujo f) =>
        (await Flujo.HistorialAsync(f.Autor, f.RevisionId)).First();

    /// <summary>Sube un PDF e inicia el flujo de una revisión (cualquiera, no solo la primera) con el revisor y aprobador del escenario.</summary>
    public async Task EnviarRevisionAsync(EscenarioFlujo f, int revisionId, string marca = "nueva")
    {
        await SubirPdfAsync(f.Autor, revisionId, marca);
        await Flujo.IniciarAsync(f.Autor, revisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]);
    }

    /// <summary>Resuelve en orden todas las etapas pendientes aprobando. La fecha de vigencia se aplica en la última.</summary>
    public async Task AprobarRevisionAsync(EscenarioFlujo f, int revisionId, DateTime? vigenciaUtc = null)
    {
        while (true)
        {
            var flujo = (await Flujo.HistorialAsync(f.Autor, revisionId)).First();
            var paso = flujo.Pasos.Where(p => p.Estado == EstadoPaso.Pendiente).OrderBy(p => p.Orden).FirstOrDefault();
            if (flujo.Estado != EstadoFlujo.EnCurso || paso is null)
            {
                return;
            }

            var decisor = paso.Clase == TipoPaso.Revision ? f.Revisor : f.Aprobador;
            await Flujo.DecidirAsync(decisor, paso.Id, aprobar: true, comentario: null, vigenciaUtc);
        }
    }

    /// <summary>Lleva la primera revisión hasta Vigente (envío + dos aprobaciones).</summary>
    public async Task PublicarPrimeraAsync(EscenarioFlujo f, DateTime? vigenciaUtc = null)
    {
        await IniciarAsync(f);
        await AprobarRevisionAsync(f, f.RevisionId, vigenciaUtc);
    }

    /// <summary>Estado de todas las revisiones del documento, por etiqueta.</summary>
    public async Task<Dictionary<string, EstadoRevision>> EstadosDelDocumentoAsync(int documentoId)
    {
        await using var db = Db();
        var revisiones = await db.Revisiones.AsNoTracking().Where(r => r.DocumentoId == documentoId).ToListAsync();
        return revisiones.GroupBy(r => r.Etiqueta).ToDictionary(g => g.Key, g => g.Last().Estado);
    }

    public async Task<EstadoRevision> EstadoRevisionAsync(int revisionId)
    {
        await using var db = Db();
        return (await db.Revisiones.AsNoTracking().SingleAsync(r => r.Id == revisionId)).Estado;
    }

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
        try
        {
            if (Directory.Exists(RutaArchivos))
            {
                Directory.Delete(RutaArchivos, recursive: true);
            }
        }
        catch (IOException)
        {
            // Limpieza de mejor esfuerzo: no debe tumbar la prueba.
        }
    }
}

/// <summary>Los actores y datos de un documento listo para recorrer el flujo.</summary>
public sealed record EscenarioFlujo(
    Actor Admin,
    Actor Autor,
    Actor Revisor,
    Actor Aprobador,
    int AreaId,
    string CodigoArea,
    int TipoId,
    DocumentoDetalleDto Documento,
    int RevisionId,
    Guid RevisorId,
    Guid AprobadorId);
