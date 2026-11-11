using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Consulta;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure;
using DocLibraryManagement.Infrastructure.Persistencia;
using DocLibraryManagement.Tests.Correo;
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

    /// <param name="correo">
    /// Activa el correo con un remitente falso (<see cref="CorreoSalida"/>). Por defecto está DESACTIVADO, como en la
    /// configuración real, así que las pruebas que no lo piden no generan notificaciones.
    /// </param>
    /// <param name="extra">Claves de configuración adicionales o que reemplazan a las del escenario.</param>
    /// <param name="remitenteReal">No sustituye el remitente: usa el que corresponda al <c>Correo:Modo</c> (para probar SMTP de verdad).</param>
    public Escenario(BaseDatosFixture bd, int tamanoMaximoMb = 50, bool correo = false, IReadOnlyDictionary<string, string?>? extra = null, bool remitenteReal = false)
    {
        Bd = bd;
        Reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
        RutaArchivos = Path.Combine(Path.GetTempPath(), "doclib-tests", Guid.NewGuid().ToString("N"));

        var valores = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = bd.Conexion,
            ["Archivos:RutaBase"] = RutaArchivos,
            ["Archivos:TamanoMaximoMb"] = tamanoMaximoMb.ToString(),
        };

        if (correo)
        {
            valores["Correo:Modo"] = "Carpeta";
            valores["Correo:CarpetaSalida"] = Path.Combine(RutaArchivos, "correo");
            valores["Correo:Remitente"] = "documentos@planta.test";
            valores["Correo:PatronCorreo"] = "{usuario}@planta.test";
            valores["Correo:UrlBase"] = "https://docs.test";
            valores["Correo:MinutosReintento"] = "5";
            valores["Correo:MaximoIntentos"] = "3";
            valores["Correo:MinutosArriendo"] = "10";
        }

        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

        var services = new ServiceCollection();
        services.AddLogging(); // el host real lo trae; este contenedor mínimo no
        services.AddInfrastructure(config);
        services.AddSingleton<TimeProvider>(Reloj);
        if (!remitenteReal)
        {
            services.AddSingleton<ICorreoSender>(CorreoSalida); // sustituye al de carpeta/SMTP: nada sale de verdad
        }
        _sp = services.BuildServiceProvider();
        _scope = _sp.CreateScope();
    }

    public BaseDatosFixture Bd { get; }

    public FakeTimeProvider Reloj { get; }

    public ICatalogoService Catalogo => _scope.ServiceProvider.GetRequiredService<ICatalogoService>();

    public IDocumentoService Documentos => _scope.ServiceProvider.GetRequiredService<IDocumentoService>();

    public IUsuarioSincronizador Usuarios => _scope.ServiceProvider.GetRequiredService<IUsuarioSincronizador>();

    public IRevisionService Revisiones => _scope.ServiceProvider.GetRequiredService<IRevisionService>();

    /// <summary>El remitente falso al que van todos los correos del escenario.</summary>
    public CorreoFalso CorreoSalida { get; } = new();

    public IReportesService Reportes => _scope.ServiceProvider.GetRequiredService<IReportesService>();

    public IAuditoriaService Auditoria => _scope.ServiceProvider.GetRequiredService<IAuditoriaService>();

    public IExportadorTablas Exportador => _scope.ServiceProvider.GetRequiredService<IExportadorTablas>();

    public IVencimientosService Vencimientos => _scope.ServiceProvider.GetRequiredService<IVencimientosService>();

    /// <summary>Mueve el reloj a «faltan N días» (negativo: ya venció hace N días) para la fecha de próxima revisión de la revisión vigente.</summary>
    public async Task IrASiguienteRevisionAsync(EscenarioFlujo f, int revisionId, double diasRestantes)
    {
        var proxima = (await Revisiones.ObtenerAsync(f.Admin, revisionId)).FechaProximaRevisionUtc
                      ?? throw new InvalidOperationException("La revisión no tiene fecha de próxima revisión.");
        Reloj.SetUtcNow(new DateTimeOffset(DateTime.SpecifyKind(proxima, DateTimeKind.Utc)) - TimeSpan.FromDays(diasRestantes));
    }

    public IColaCorreoService Cola => _scope.ServiceProvider.GetRequiredService<IColaCorreoService>();

    public IUsuariosService UsuariosAdmin => _scope.ServiceProvider.GetRequiredService<IUsuariosService>();

    /// <summary>Procesa la cola con un tope alto: las notificaciones de otras pruebas de la clase no la deben acaparar.</summary>
    public Task<int> ProcesarColaAsync() => Cola.ProcesarPendientesAsync(maximo: 5000);

    /// <summary>Todas las notificaciones de un documento, de la más antigua a la más nueva.</summary>
    public async Task<List<Notificacion>> NotificacionesDeAsync(string codigoDocumento)
    {
        await using var db = Db();
        return await db.Notificaciones.AsNoTracking().Include(n => n.Destinatario)
            .Where(n => n.CodigoDocumento == codigoDocumento).OrderBy(n => n.Id).ToListAsync();
    }

    public IConsultaService Consulta() => _scope.ServiceProvider.GetRequiredService<IConsultaService>();

    public IFlujoService Flujo => _scope.ServiceProvider.GetRequiredService<IFlujoService>();

    public string RutaArchivos { get; }

    /// <summary>Proveedor del ámbito del escenario, para resolver servicios que no tienen atajo propio.</summary>
    public IServiceProvider Scope() => _scope.ServiceProvider;

    /// <summary>
    /// PDF válido con texto «Texto de prueba {marca}». Cada marca distinta da contenido (y hash) distinto, y la
    /// marca es lo que luego se puede buscar.
    /// </summary>
    public static byte[] PdfDePrueba(string marca = "base") => GeneradorPdf.Crear($"Texto de prueba {marca}");

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

    /// <summary>Crea otro documento en el área del escenario, con el título y el texto de PDF indicados, y lo deja Vigente.</summary>
    public async Task<(DocumentoDetalleDto Doc, int RevisionId)> PublicarOtroAsync(EscenarioFlujo f, string titulo, string textoPdf)
    {
        var doc = await Documentos.CrearAsync(f.Autor, new NuevoDocumento(titulo, f.AreaId, null, f.TipoId));
        var revisionId = doc.Revisiones.Single().Id;
        await SubirPdfAsync(f.Autor, revisionId, textoPdf);
        await Flujo.IniciarAsync(f.Autor, revisionId, [new AsignacionPaso(1, f.RevisorId), new AsignacionPaso(2, f.AprobadorId)]);
        await AprobarRevisionAsync(f, revisionId);
        return (doc, revisionId);
    }

    /// <summary>Palabra única y sin significado, para que la búsqueda de una prueba no choque con las de otras.</summary>
    public static string PalabraUnica() => "zq" + Guid.NewGuid().ToString("N")[..10];

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
