using System.Security.Claims;
using DocLibraryManagement.Application.Seguridad;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Seguridad;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocLibraryManagement.Tests.Seguridad;

public class PoliticasWebTests
{
    private static ServiceProvider Proveedor(string modo = ConfiguracionSeguridad.ModoDesarrollo, string entorno = "Development")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Autenticacion:Modo"] = modo })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSeguridadDocumental(config, new FakeEntorno(entorno));
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Usuario(RolSistema[] roles, params string[] areas)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "DEV\\prueba") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        claims.AddRange(areas.Select(a => new Claim(ClaimsDoc.Area, a)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"));
    }

    private static async Task<bool> PoliticaAsync(ClaimsPrincipal usuario, string politica)
    {
        using var sp = Proveedor();
        var auth = sp.GetRequiredService<IAuthorizationService>();
        return (await auth.AuthorizeAsync(usuario, politica)).Succeeded;
    }

    [Theory]
    [InlineData(RolSistema.Lector, Politicas.PuedeAdministrar, false)]
    [InlineData(RolSistema.Autor, Politicas.PuedeAdministrar, false)]
    [InlineData(RolSistema.Administrador, Politicas.PuedeAdministrar, true)]
    [InlineData(RolSistema.Auditor, Politicas.PuedeAuditar, true)]
    [InlineData(RolSistema.Lector, Politicas.PuedeAuditar, false)]
    [InlineData(RolSistema.Administrador, Politicas.PuedeAuditar, true)]
    [InlineData(RolSistema.Aprobador, Politicas.PuedeAprobar, true)]
    [InlineData(RolSistema.Revisor, Politicas.PuedeAprobar, false)]
    [InlineData(RolSistema.Revisor, Politicas.PuedeRevisar, true)]
    [InlineData(RolSistema.Aprobador, Politicas.PuedeRevisar, false)]
    [InlineData(RolSistema.Autor, Politicas.PuedeEditarDocumentos, true)]
    [InlineData(RolSistema.Lector, Politicas.PuedeEditarDocumentos, false)]
    public async Task Las_politicas_respetan_el_rol(RolSistema rol, string politica, bool esperado)
    {
        var usuario = Usuario([RolSistema.Lector, rol]);

        Assert.Equal(esperado, await PoliticaAsync(usuario, politica));
    }

    [Fact]
    public async Task Un_Aprobador_no_administra()
    {
        // El rol Aprobador no hereda permisos de administración.
        Assert.False(await PoliticaAsync(Usuario([RolSistema.Aprobador]), Politicas.PuedeAdministrar));
    }

    [Fact]
    public async Task Un_usuario_anonimo_no_pasa_la_politica_por_defecto()
    {
        using var sp = Proveedor();
        var auth = sp.GetRequiredService<IAuthorizationService>();
        var opciones = sp.GetRequiredService<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
        var fallback = await opciones.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        var anonimo = new ClaimsPrincipal(new ClaimsIdentity());
        Assert.False((await auth.AuthorizeAsync(anonimo, null, fallback)).Succeeded);
        Assert.True((await auth.AuthorizeAsync(Usuario([RolSistema.Lector]), null, fallback)).Succeeded);
    }

    [Theory]
    [InlineData("CAL", true)]
    [InlineData("MAN", false)]
    public async Task Un_Autor_solo_edita_documentos_de_su_area(string areaDelDocumento, bool esperado)
    {
        using var sp = Proveedor();
        var auth = sp.GetRequiredService<IAuthorizationService>();
        var autor = Usuario([RolSistema.Lector, RolSistema.Autor], "CAL");

        var resultado = await auth.AuthorizeAsync(autor, areaDelDocumento, new EdicionEnAreaRequirement());

        Assert.Equal(esperado, resultado.Succeeded);
    }

    [Fact]
    public async Task Un_Administrador_edita_en_cualquier_area()
    {
        using var sp = Proveedor();
        var auth = sp.GetRequiredService<IAuthorizationService>();

        var resultado = await auth.AuthorizeAsync(Usuario([RolSistema.Administrador]), "XYZ", new EdicionEnAreaRequirement());

        Assert.True(resultado.Succeeded);
    }

    [Fact]
    public void El_modo_Desarrollo_esta_prohibido_fuera_de_Development()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Proveedor(ConfiguracionSeguridad.ModoDesarrollo, Environments.Production));
    }

    [Fact]
    public void Un_modo_desconocido_falla_al_arrancar()
    {
        Assert.Throws<InvalidOperationException>(() => Proveedor("Otro"));
    }

    [Fact]
    public void El_modo_Windows_es_valido_en_produccion()
    {
        using var sp = Proveedor(ConfiguracionSeguridad.ModoWindows, Environments.Production);

        Assert.NotNull(sp.GetService<IAuthorizationService>());
    }

    private sealed class FakeEntorno(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "Pruebas";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
