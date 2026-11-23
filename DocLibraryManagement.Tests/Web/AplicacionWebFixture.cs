using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using DocLibraryManagement.Tests.Datos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// La aplicación REAL en memoria, en modo Desarrollo (usuarios simulados), sobre una BD y una carpeta
/// de archivos temporales. Arranca una vez por clase de prueba y deja sembrados:
/// <list type="bullet">
/// <item>Un documento en flujo, esperando al revisor (<see cref="DocumentoId"/>).</item>
/// <item>Un documento PUBLICADO con una revisión obsoleta (Rev.00) y una vigente (Rev.01).</item>
/// <item>Un documento al que solo le falta la aprobación final (<see cref="DocumentoEnAprobacionId"/>).</item>
/// </list>
/// </summary>
public sealed class AplicacionWebFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _baseDatos = $"DocLibrary_Web_{Guid.NewGuid():N}";
    private readonly string _carpetaArchivos = Path.Combine(Path.GetTempPath(), "doclib-web-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Texto distintivo que solo aparece en el PDF del documento en flujo.</summary>
    public const string TextoEnFlujo = "Procedimiento de arranque de línea con bloqueo de energía";

    /// <summary>Texto del PDF de la revisión VIGENTE del documento publicado.</summary>
    public const string TextoVigente = "Instructivo de calibración de balanzas con verificación semanal";

    /// <summary>Texto del PDF de la revisión OBSOLETA del documento publicado.</summary>
    public const string TextoObsoleto = "Instructivo antiguo de pesaje con tolerancia ampliada";

    public byte[] ContenidoPdf { get; } = GeneradorPdf.Crear(TextoEnFlujo);

    public byte[] ContenidoPdfVigente { get; } = GeneradorPdf.Crear(TextoVigente);

    public int RevisionId { get; private set; }

    public int DocumentoId { get; private set; }

    public string CodigoDocumento { get; private set; } = "";

    public int DocumentoPublicadoId { get; private set; }

    /// <summary>La revisión vigente (Rev.01) del documento publicado.</summary>
    public int RevisionPublicadaId { get; private set; }

    public int RevisionObsoletaId { get; private set; }

    public string CodigoPublicado { get; private set; } = "";

    public string TituloPublicado => "Instructivo publicado";

    public int DocumentoEnAprobacionId { get; private set; }

    public string CodigoEnAprobacion { get; private set; } = "";

    public int AreaId { get; private set; }

    public const string TituloProgramado = "Procedimiento programado";

    /// <summary>Primera emisión aprobada con vigencia a 30 días: estado Aprobado, todavía no vigente.</summary>
    public int DocumentoProgramadoId { get; private set; }

    public int RevisionProgramadaId { get; private set; }

    public string CodigoProgramado { get; private set; } = "";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            $"Server=(localdb)\\mssqllocaldb;Database={_baseDatos};Trusted_Connection=True");
        builder.UseSetting("Archivos:RutaBase", _carpetaArchivos);
        builder.UseSetting("TareasDeFondo:MinutosEntreEjecuciones", "0"); // sin proceso de fondo: las pruebas controlan cuándo corre
        // appsettings.Development.json activa el modo Carpeta; aquí se apaga para que las pruebas web no escriban .eml
        // en la carpeta de la aplicación ni arranque el envío en segundo plano.
        builder.UseSetting("Correo:Modo", "Desactivado");
        builder.UseSetting("Correo:SegundosEntreEnvios", "0");
    }

    /// <summary>Cliente que conserva cookies (sesión) y NO sigue redirecciones, para ver cada respuesta tal cual.</summary>
    public HttpClient CrearCliente() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public async Task<HttpClient> ClienteComoAsync(string persona)
    {
        var cliente = CrearCliente();
        var respuesta = await cliente.PostAsync($"/dev/login/{persona}", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Redirect, respuesta.StatusCode);
        return cliente;
    }

    public async Task InitializeAsync()
    {
        // Forzar el arranque: en Development la aplicación crea y migra la BD al iniciar.
        using var _ = CrearCliente();

        using var scope = Services.CreateScope();
        var sp = scope.ServiceProvider;
        var usuarios = sp.GetRequiredService<IUsuarioSincronizador>();

        // Los mismos identificadores y roles que las personas de appsettings.Development.json.
        Actor Persona(string id, RolSistema[] roles, params string[] areas) =>
            new($"DEV\\{id}", roles.Append(RolSistema.Lector).ToHashSet(), areas.ToHashSet(StringComparer.OrdinalIgnoreCase));

        var admin = Persona("admin", [RolSistema.Administrador]);
        var autor = Persona("autor-cal", [RolSistema.Autor], "CAL");
        var revisor = Persona("revisor", [RolSistema.Revisor]);
        var aprobador = Persona("aprobador", [RolSistema.Aprobador]);

        var ids = new Dictionary<string, Guid>();
        foreach (var p in new[] { admin, autor, revisor, aprobador, Persona("lector", []), Persona("auditor", [RolSistema.Auditor]) })
        {
            var u = await usuarios.SincronizarAsync(p.Identificador, p.Identificador, null, p.Roles.ToList(), p.Areas.ToList());
            ids[p.Identificador] = u.Id;
        }

        var catalogo = sp.GetRequiredService<ICatalogoService>();
        var areaId = await catalogo.CrearAreaAsync(admin, "CAL", "Calidad");
        AreaId = areaId;
        await catalogo.CrearSubAreaAsync(admin, areaId, "GEN", "Unidad general"); // la unidad forma parte del código del documento
        var tipoId = await catalogo.CrearTipoAsync(admin, "PRO", "Procedimiento", 12);
        await catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [tipoId]);
        // El aprobador es también dueño del área (aprueba las revisiones menores).
        await catalogo.EstablecerDuenoDeAreaAsync(admin, areaId, ids["DEV\\aprobador"]);

        var flujo = sp.GetRequiredService<IFlujoService>();
        await flujo.GuardarPlantillaAsync(admin, tipoId,
        [
            new PasoPlantillaDto(1, TipoPaso.Revision, "Revisión técnica"),
            new PasoPlantillaDto(2, TipoPaso.Aprobacion, "Aprobación"),
        ]);

        var documentos = sp.GetRequiredService<IDocumentoService>();
        var revisiones = sp.GetRequiredService<IRevisionService>();
        var asignaciones = new List<AsignacionPaso> { new(1, ids["DEV\\revisor"]), new(2, ids["DEV\\aprobador"]) };

        async Task SubirPdfAsync(int revId, byte[] contenido, string nombre = "x.pdf")
        {
            using var s = new MemoryStream(contenido);
            await revisiones.SubirArchivoAsync(autor, revId, TipoArchivo.Pdf, nombre, s);
        }

        async Task<IReadOnlyList<PasoInstanciaDto>> EnviarAsync(int revId)
        {
            await flujo.IniciarAsync(autor, revId, asignaciones);
            return (await flujo.HistorialAsync(autor, revId)).First().Pasos;
        }

        async Task AprobarTodoAsync(int revId, DateTime? vigencia = null)
        {
            var pasos = (await flujo.HistorialAsync(autor, revId)).First().Pasos;
            await flujo.DecidirAsync(revisor, pasos[0].Id, true, null);
            await flujo.DecidirAsync(aprobador, pasos[1].Id, true, null, vigencia);
        }

        // 1) Un documento en flujo, esperando al revisor.
        var enFlujo = await documentos.CrearAsync(autor, new NuevoDocumento("Procedimiento de arranque", areaId, null, tipoId));
        DocumentoId = enFlujo.Id;
        CodigoDocumento = enFlujo.Codigo;
        RevisionId = enFlujo.Revisiones.Single().Id;
        await SubirPdfAsync(RevisionId, ContenidoPdf, "arranque.pdf");
        using (var fuente = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9]))
        {
            await revisiones.SubirArchivoAsync(autor, RevisionId, TipoArchivo.Fuente, "arranque.docx", fuente);
        }

        await EnviarAsync(RevisionId);

        // 2) Un documento publicado: Rev.00 quedó obsoleta y Rev.01 es la vigente.
        var publicado = await documentos.CrearAsync(autor, new NuevoDocumento(TituloPublicado, areaId, null, tipoId));
        DocumentoPublicadoId = publicado.Id;
        CodigoPublicado = publicado.Codigo;
        RevisionObsoletaId = publicado.Revisiones.Single().Id;
        await SubirPdfAsync(RevisionObsoletaId, GeneradorPdf.Crear(TextoObsoleto));
        await EnviarAsync(RevisionObsoletaId);
        await AprobarTodoAsync(RevisionObsoletaId);

        var nueva = await revisiones.CrearRevisionAsync(autor, publicado.Id, TipoRevisionNueva.Mayor, "Se agrega verificación semanal", null, false);
        RevisionPublicadaId = nueva.Id;
        await SubirPdfAsync(nueva.Id, ContenidoPdfVigente);
        await EnviarAsync(nueva.Id);
        await AprobarTodoAsync(nueva.Id);

        // 3) Un documento al que solo le falta la aprobación final: es el turno del aprobador.
        var enAprobacion = await documentos.CrearAsync(autor, new NuevoDocumento("Procedimiento por aprobar", areaId, null, tipoId));
        DocumentoEnAprobacionId = enAprobacion.Id;
        CodigoEnAprobacion = enAprobacion.Codigo;
        var revAprobacion = enAprobacion.Revisiones.Single().Id;
        await SubirPdfAsync(revAprobacion, GeneradorPdf.Crear("Procedimiento que espera su aprobación final"));
        var pasosAprobacion = await EnviarAsync(revAprobacion);
        await flujo.DecidirAsync(revisor, pasosAprobacion[0].Id, true, null);

        // 4) Una primera emisión ya aprobada pero con vigencia FUTURA: aún no es vigente (no sale en la lista maestra).
        var programado = await documentos.CrearAsync(autor, new NuevoDocumento(TituloProgramado, areaId, null, tipoId));
        DocumentoProgramadoId = programado.Id;
        CodigoProgramado = programado.Codigo;
        RevisionProgramadaId = programado.Revisiones.Single().Id;
        await SubirPdfAsync(RevisionProgramadaId, GeneradorPdf.Crear("Procedimiento programado para entrar en vigor más adelante"));
        await EnviarAsync(RevisionProgramadaId);
        await AprobarTodoAsync(RevisionProgramadaId, DateTime.UtcNow.AddDays(30));
    }

    public new async Task DisposeAsync()
    {
        try
        {
            var opciones = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer($"Server=(localdb)\\mssqllocaldb;Database={_baseDatos};Trusted_Connection=True").Options;
            await using var db = new AppDbContext(opciones);
            await db.Database.EnsureDeletedAsync();
        }
        finally
        {
            await base.DisposeAsync();
            if (Directory.Exists(_carpetaArchivos))
            {
                Directory.Delete(_carpetaArchivos, recursive: true);
            }
        }
    }
}
