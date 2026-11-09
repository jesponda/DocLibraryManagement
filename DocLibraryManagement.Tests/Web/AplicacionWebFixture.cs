using DocLibraryManagement.Application.Archivos;
using DocLibraryManagement.Application.Catalogo;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// La aplicación REAL en memoria, en modo Desarrollo (usuarios simulados), sobre una BD y una carpeta
/// de archivos temporales. Arranca una vez por clase de prueba y deja sembrado un documento con PDF
/// en flujo de aprobación para ejercitar páginas y descargas.
/// </summary>
public sealed class AplicacionWebFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _baseDatos = $"DocLibrary_Web_{Guid.NewGuid():N}";
    private readonly string _carpetaArchivos = Path.Combine(Path.GetTempPath(), "doclib-web-tests", Guid.NewGuid().ToString("N"));

    public byte[] ContenidoPdf { get; } = Escenario_Pdf();

    public int RevisionId { get; private set; }

    public int DocumentoId { get; private set; }

    public string CodigoDocumento { get; private set; } = "";

    public int DocumentoPublicadoId { get; private set; }

    public int RevisionPublicadaId { get; private set; }

    public string CodigoPublicado { get; private set; } = "";

    public int DocumentoEnAprobacionId { get; private set; }

    public string CodigoEnAprobacion { get; private set; } = "";

    private static byte[] Escenario_Pdf() =>
        System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% prueba web\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection",
            $"Server=(localdb)\\mssqllocaldb;Database={_baseDatos};Trusted_Connection=True");
        builder.UseSetting("Archivos:RutaBase", _carpetaArchivos);
        builder.UseSetting("Publicacion:MinutosEntreRevisiones", "0"); // sin proceso de fondo: las pruebas controlan cuándo corre
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
        var tipoId = await catalogo.CrearTipoAsync(admin, "PRO", "Procedimiento", 12);
        await catalogo.EstablecerTiposDeAreaAsync(admin, areaId, [tipoId]);

        var flujo = sp.GetRequiredService<IFlujoService>();
        await flujo.GuardarPlantillaAsync(admin, tipoId,
        [
            new PasoPlantillaDto(1, TipoPaso.Revision, "Revisión técnica"),
            new PasoPlantillaDto(2, TipoPaso.Aprobacion, "Aprobación"),
        ]);

        var doc = await sp.GetRequiredService<IDocumentoService>().CrearAsync(autor, new NuevoDocumento("Procedimiento de arranque", areaId, null, tipoId));
        DocumentoId = doc.Id;
        CodigoDocumento = doc.Codigo;
        RevisionId = doc.Revisiones.Single().Id;

        using (var pdf = new MemoryStream(ContenidoPdf))
        {
            await sp.GetRequiredService<IRevisionService>().SubirArchivoAsync(autor, RevisionId, TipoArchivo.Pdf, "arranque.pdf", pdf);
        }

        using var fuente = new MemoryStream([0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9]);
        await sp.GetRequiredService<IRevisionService>().SubirArchivoAsync(autor, RevisionId, TipoArchivo.Fuente, "arranque.docx", fuente);

        await flujo.IniciarAsync(autor, RevisionId,
            [new AsignacionPaso(1, ids["DEV\\revisor"]), new AsignacionPaso(2, ids["DEV\\aprobador"])]);

        // El aprobador es también dueño del área (aprueba las revisiones menores).
        await catalogo.EstablecerDuenoDeAreaAsync(admin, areaId, ids["DEV\\aprobador"]);

        var documentos = sp.GetRequiredService<IDocumentoService>();
        var revisiones = sp.GetRequiredService<IRevisionService>();

        async Task<(DocumentoDetalleDto Doc, int RevisionId, IReadOnlyList<PasoInstanciaDto> Pasos)> EnviarNuevoAsync(string titulo)
        {
            var d = await documentos.CrearAsync(autor, new NuevoDocumento(titulo, areaId, null, tipoId));
            var revId = d.Revisiones.Single().Id;
            using var pdfNuevo = new MemoryStream(ContenidoPdf);
            await revisiones.SubirArchivoAsync(autor, revId, TipoArchivo.Pdf, "x.pdf", pdfNuevo);
            await flujo.IniciarAsync(autor, revId, [new AsignacionPaso(1, ids["DEV\\revisor"]), new AsignacionPaso(2, ids["DEV\\aprobador"])]);
            return (d, revId, (await flujo.HistorialAsync(autor, revId)).First().Pasos);
        }

        // Un documento ya publicado (vigente).
        var publicado = await EnviarNuevoAsync("Instructivo publicado");
        await flujo.DecidirAsync(revisor, publicado.Pasos[0].Id, true, null);
        await flujo.DecidirAsync(aprobador, publicado.Pasos[1].Id, true, null);
        DocumentoPublicadoId = publicado.Doc.Id;
        RevisionPublicadaId = publicado.RevisionId;
        CodigoPublicado = publicado.Doc.Codigo;

        // Un documento al que solo le falta la aprobación final: es el turno del aprobador.
        var enAprobacion = await EnviarNuevoAsync("Procedimiento por aprobar");
        await flujo.DecidirAsync(revisor, enAprobacion.Pasos[0].Id, true, null);
        DocumentoEnAprobacionId = enAprobacion.Doc.Id;
        CodigoEnAprobacion = enAprobacion.Doc.Codigo;
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
