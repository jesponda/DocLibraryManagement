using System.Net;
using System.Text.RegularExpressions;
using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure.Persistencia;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// La aplicación REAL en modo de acceso «Local» (lista de usuarios con contraseña), con su BD temporal. Arranca con el
/// Administrador inicial «admin» y su contraseña temporal, tal como la primera vez en producción.
/// </summary>
public sealed class AplicacionWebLocalFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _baseDatos = $"DocLibrary_WebLocal_{Guid.NewGuid():N}";
    private readonly string _carpetaArchivos = Path.Combine(Path.GetTempPath(), "doclib-web-tests", Guid.NewGuid().ToString("N"));

    public const string ContrasenaInicial = "Temporal-Admin-1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:DefaultConnection", $"Server=(localdb)\\mssqllocaldb;Database={_baseDatos};Trusted_Connection=True");
        builder.UseSetting("Archivos:RutaBase", _carpetaArchivos);
        builder.UseSetting("TareasDeFondo:MinutosEntreEjecuciones", "0");
        builder.UseSetting("Correo:Modo", "Desactivado");
        builder.UseSetting("Correo:SegundosEntreEnvios", "0");
        builder.UseSetting("Autenticacion:Modo", "Local");
        builder.UseSetting("Autenticacion:AdministradorInicial:Usuario", "admin");
        builder.UseSetting("Autenticacion:AdministradorInicial:ContrasenaTemporal", ContrasenaInicial);
    }

    public HttpClient CrearCliente() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public Task InitializeAsync()
    {
        using var _ = CrearCliente(); // fuerza el arranque: migra la BD y crea al Administrador inicial
        return Task.CompletedTask;
    }

    /// <summary>Actor «Administrador» para dar de alta cuentas por la capa de servicios.</summary>
    public static Actor Admin { get; } = new("admin", new HashSet<RolSistema> { RolSistema.Administrador, RolSistema.Lector }, new HashSet<string>());

    public async Task<ResultadoAlta> CrearCuentaAsync(string usuario, params RolSistema[] roles)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICuentasService>()
            .CrearAsync(Admin, usuario, new DatosCuenta($"Persona {usuario}", null, roles, []));
    }

    /// <summary>Entra con usuario y contraseña como lo haría el navegador (formulario con antiforgery). Devuelve la respuesta del POST.</summary>
    public async Task<(HttpClient Cliente, HttpResponseMessage Respuesta)> EntrarAsync(string usuario, string contrasena, string? retorno = null)
    {
        var cliente = CrearCliente();
        var pagina = await cliente.GetStringAsync("/acceso");
        var respuesta = await cliente.PostAsync("/acceso/entrar", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token(pagina)),
            new("usuario", usuario),
            new("contrasena", contrasena),
            new("retorno", retorno ?? ""),
        ]));
        return (cliente, respuesta);
    }

    /// <summary>Sesión iniciada; si se da una contraseña nueva, se cambia (como exige el primer acceso con la temporal).</summary>
    public async Task<HttpClient> SesionAsync(string usuario, string contrasenaActual, string? contrasenaNueva = null)
    {
        var (cliente, respuesta) = await EntrarAsync(usuario, contrasenaActual);
        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        if (contrasenaNueva is not null)
        {
            var cambio = await CambiarContrasenaAsync(cliente, contrasenaActual, contrasenaNueva, contrasenaNueva);
            Assert.StartsWith("/mi-acceso", cambio.Headers.Location!.ToString());
        }

        return cliente;
    }

    public async Task<HttpResponseMessage> CambiarContrasenaAsync(HttpClient cliente, string actual, string nueva, string confirmar)
    {
        var pagina = await cliente.GetStringAsync("/cuenta/contrasena");
        return await cliente.PostAsync("/cuenta/contrasena/cambiar", new FormUrlEncodedContent(
        [
            new("__RequestVerificationToken", Token(pagina)),
            new("actual", actual),
            new("nueva", nueva),
            new("confirmar", confirmar),
        ]));
    }

    public static string Token(string html)
    {
        var m = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""");
        return m.Success ? m.Groups[1].Value : throw new InvalidOperationException("La página no trae token antiforgery.");
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
