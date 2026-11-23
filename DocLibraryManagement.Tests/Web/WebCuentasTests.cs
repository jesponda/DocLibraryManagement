using System.Net;
using System.Text.RegularExpressions;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Web;

/// <summary>
/// El acceso con usuario y contraseña, por HTTP y contra la aplicación real: quién entra, quién queda fuera, el cambio
/// obligatorio de la contraseña temporal y que desactivar a alguien o cambiarle el rol surte efecto de inmediato.
/// </summary>
public class WebCuentasTests(AplicacionWebLocalFixture app) : IClassFixture<AplicacionWebLocalFixture>
{
    private static string Texto(string html) =>
        Regex.Replace(Regex.Replace(WebUtility.HtmlDecode(html), "<[^>]+>", " "), @"\s+", " ").Trim();

    private static string Id() => $"u{Guid.NewGuid():N}"[..12];

    private const string Clave = "Mi-clave-segura-2026";

    /// <summary>A dónde redirige la respuesta, como ruta (la autenticación por cookie la devuelve absoluta).</summary>
    private static string Destino(HttpResponseMessage r) =>
        r.Headers.Location!.IsAbsoluteUri ? r.Headers.Location.PathAndQuery : r.Headers.Location.OriginalString;

    /// <summary>Alta por el servicio y primer acceso con cambio de contraseña, dejando una sesión normal.</summary>
    private async Task<(string Usuario, HttpClient Cliente)> PersonaAsync(params RolSistema[] roles)
    {
        var usuario = Id();
        var alta = await app.CrearCuentaAsync(usuario, roles);
        return (usuario, await app.SesionAsync(usuario, alta.ContrasenaTemporal, Clave));
    }

    // ---------- acceso ----------

    [Fact]
    public async Task Sin_sesion_se_pide_iniciar_sesion_y_se_recuerda_a_donde_iba()
    {
        using var cliente = app.CrearCliente();

        using var r = await cliente.GetAsync("/lista-maestra?texto=algo");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/acceso?", Destino(r));
        Assert.Contains("ReturnUrl", Destino(r));
    }

    [Fact]
    public async Task La_pantalla_de_acceso_es_publica_trae_el_formulario_con_antiforgery_y_sus_estilos()
    {
        using var cliente = app.CrearCliente();

        using var r = await cliente.GetAsync("/acceso");
        var html = await r.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("action=\"acceso/entrar\"", html);
        Assert.Contains("name=\"usuario\"", html);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Matches(@"href=""css/sgd-tema\.[^""]+\.css""", html);
        Assert.DoesNotContain("Cerrar sesión", Texto(html));
    }

    [Fact]
    public async Task Una_contrasena_incorrecta_vuelve_al_acceso_con_un_mensaje_generico()
    {
        var (cliente, r) = await app.EntrarAsync("admin", "no-es-la-contrasena");
        using var _ = cliente;

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Contains("/acceso?e=rechazo", r.Headers.Location!.ToString());
        var pagina = Texto(await cliente.GetStringAsync(r.Headers.Location));
        Assert.Contains("Usuario o contraseña incorrectos", pagina);
        using var interior = await cliente.GetAsync("/lista-maestra"); // y no entró: sigue sin sesión
        Assert.Equal(HttpStatusCode.Redirect, interior.StatusCode);
    }

    [Fact]
    public async Task Un_usuario_que_no_existe_recibe_exactamente_el_mismo_mensaje()
    {
        var (c1, existente) = await app.EntrarAsync("admin", "mala");
        var (c2, inexistente) = await app.EntrarAsync("nadie-asi", "mala");
        using var _1 = c1;
        using var _2 = c2;

        Assert.Equal(existente.Headers.Location, inexistente.Headers.Location);
    }

    [Fact]
    public async Task Un_envio_sin_token_antiforgery_se_rechaza()
    {
        using var cliente = app.CrearCliente();

        using var r = await cliente.PostAsync("/acceso/entrar", new FormUrlEncodedContent([new("usuario", "admin"), new("contrasena", AplicacionWebLocalFixture.ContrasenaInicial)]));

        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("//sitio-ajeno.com/robar")]
    [InlineData("https://sitio-ajeno.com")]
    [InlineData("/\\sitio-ajeno.com")]
    public async Task Despues_de_entrar_no_se_redirige_a_sitios_ajenos(string retorno)
    {
        var (usuario, _) = await PersonaAsync();

        var (cliente, r) = await app.EntrarAsync(usuario, Clave, retorno);
        using var _ = cliente;

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Despues_de_entrar_se_vuelve_a_la_pagina_pedida_si_es_de_esta_aplicacion()
    {
        var (usuario, _) = await PersonaAsync();

        var (cliente, r) = await app.EntrarAsync(usuario, Clave, "/lista-maestra?texto=x");
        using var _ = cliente;

        Assert.Equal("/lista-maestra?texto=x", r.Headers.Location!.ToString());
    }

    // ---------- contraseña temporal ----------

    [Fact]
    public async Task Con_una_contrasena_temporal_solo_se_puede_cambiarla_hasta_hacerlo()
    {
        var usuario = Id();
        var alta = await app.CrearCuentaAsync(usuario, RolSistema.Revisor);
        var (cliente, entrada) = await app.EntrarAsync(usuario, alta.ContrasenaTemporal);
        using var _ = cliente;
        Assert.Equal("/cuenta/contrasena", entrada.Headers.Location!.ToString());

        foreach (var ruta in new[] { "/", "/lista-maestra", "/pendientes", "/mi-acceso" })
        {
            using var r = await cliente.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Equal("/cuenta/contrasena", r.Headers.Location!.ToString());
        }

        var pagina = await cliente.GetStringAsync("/cuenta/contrasena"); // la pantalla de cambio sí abre, con sus estilos
        Assert.Contains("Entraste con una contraseña temporal", Texto(pagina));
        var css = Regex.Match(pagina, @"href=""(css/sgd-tema\.[^""]+\.css)""").Groups[1].Value;
        using (var estilos = await cliente.GetAsync("/" + css))
        {
            Assert.Equal(HttpStatusCode.OK, estilos.StatusCode);
        }

        var cambio = await app.CambiarContrasenaAsync(cliente, alta.ContrasenaTemporal, Clave, Clave);
        Assert.StartsWith("/mi-acceso", cambio.Headers.Location!.ToString());
        using var ya = await cliente.GetAsync("/lista-maestra");
        Assert.Equal(HttpStatusCode.OK, ya.StatusCode);
    }

    [Fact]
    public async Task El_cambio_de_contrasena_explica_los_errores_sin_aceptar_texto_inventado_en_la_URL()
    {
        var (_, cliente) = await PersonaAsync();
        using var _ = cliente;

        var distintas = await app.CambiarContrasenaAsync(cliente, Clave, "Otra-clave-segura-1", "Otra-clave-segura-2");
        var debil = await app.CambiarContrasenaAsync(cliente, Clave, "corta", "corta");
        var actualMala = await app.CambiarContrasenaAsync(cliente, "no-es", "Otra-clave-segura-1", "Otra-clave-segura-1");

        Assert.Contains("no coinciden", Texto(await cliente.GetStringAsync(distintas.Headers.Location)));
        Assert.Contains("al menos 10", Texto(await cliente.GetStringAsync(debil.Headers.Location)));
        Assert.Contains("actual no es correcta", Texto(await cliente.GetStringAsync(actualMala.Headers.Location)));

        // Un mensaje escrito a mano en la dirección no se muestra (solo los que el servidor firmó).
        var falso = Texto(await cliente.GetStringAsync("/cuenta/contrasena?m=Llama%20al%20555-1234"));
        Assert.DoesNotContain("555-1234", falso);
    }

    [Fact]
    public async Task Cambiar_la_contrasena_cierra_las_otras_sesiones_de_la_misma_cuenta()
    {
        var usuario = Id();
        var alta = await app.CrearCuentaAsync(usuario);
        var (otra, _) = await app.EntrarAsync(usuario, alta.ContrasenaTemporal); // sesión en «otro equipo»
        using var _o = otra;
        var cliente = await app.SesionAsync(usuario, alta.ContrasenaTemporal, Clave);
        using var _c = cliente;

        using var r = await otra.GetAsync("/cuenta/contrasena");

        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("/acceso", Destino(r));
        using var propia = await cliente.GetAsync("/lista-maestra");
        Assert.Equal(HttpStatusCode.OK, propia.StatusCode);
    }

    // ---------- efecto inmediato de la administración ----------

    [Fact]
    public async Task Desactivar_a_alguien_le_cierra_la_sesion_en_la_siguiente_solicitud()
    {
        var (usuario, cliente) = await PersonaAsync();
        using var _ = cliente;
        using (var antes = await cliente.GetAsync("/lista-maestra"))
        {
            Assert.Equal(HttpStatusCode.OK, antes.StatusCode);
        }

        using (var scope = app.Services.CreateScope())
        {
            var cuentas = scope.ServiceProvider.GetRequiredService<ICuentasService>();
            var id = (await cuentas.ListarAsync(AplicacionWebLocalFixture.Admin)).Single(c => c.Identificador == usuario).Id;
            await cuentas.CambiarEstadoAsync(AplicacionWebLocalFixture.Admin, id, false);
        }

        using var despues = await cliente.GetAsync("/lista-maestra");
        Assert.Equal(HttpStatusCode.Redirect, despues.StatusCode);
        Assert.StartsWith("/acceso", Destino(despues));
        var (c2, r) = await app.EntrarAsync(usuario, Clave);
        using var _2 = c2;
        Assert.Contains("e=rechazo", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Un_cambio_de_rol_se_nota_sin_volver_a_entrar()
    {
        var (usuario, cliente) = await PersonaAsync();
        using var _ = cliente;
        var antes = await cliente.GetStringAsync("/");
        Assert.DoesNotContain("href=\"pendientes\"", antes);

        using (var scope = app.Services.CreateScope())
        {
            var cuentas = scope.ServiceProvider.GetRequiredService<ICuentasService>();
            var cuenta = (await cuentas.ListarAsync(AplicacionWebLocalFixture.Admin)).Single(c => c.Identificador == usuario);
            await cuentas.ActualizarAsync(AplicacionWebLocalFixture.Admin, cuenta.Id, new DatosCuenta(cuenta.Nombre, null, [RolSistema.Revisor], []));
        }

        var despues = await cliente.GetStringAsync("/");
        Assert.Contains("href=\"pendientes\"", despues);
    }

    // ---------- pantallas ----------

    [Fact]
    public async Task La_pantalla_de_usuarios_es_solo_del_Administrador_y_lista_a_las_personas()
    {
        var persona = Id();
        await app.CrearCuentaAsync(persona, RolSistema.Auditor);
        var admin = await app.SesionAsync("admin", AplicacionWebLocalFixture.ContrasenaInicial, "Clave-del-jefe-2026");
        var (_, auditor) = await PersonaAsync(RolSistema.Auditor);
        var (_, lector) = await PersonaAsync();
        using var _a = admin;
        using var _b = auditor;
        using var _c = lector;

        using var delAdmin = await admin.GetAsync("/administracion/usuarios");
        using var delAuditor = await auditor.GetAsync("/administracion/usuarios");
        using var delLector = await lector.GetAsync("/administracion/usuarios");

        Assert.Equal(HttpStatusCode.OK, delAdmin.StatusCode);
        var texto = Texto(await delAdmin.Content.ReadAsStringAsync());
        Assert.Contains("Usuarios y roles", texto);
        Assert.Contains(persona, texto);
        Assert.Contains("Nuevo usuario", texto);
        foreach (var r in new[] { delAuditor, delLector })
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("acceso-denegado", r.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task El_encabezado_ofrece_cerrar_sesion_y_al_hacerlo_ya_no_se_entra()
    {
        var (_, cliente) = await PersonaAsync();
        using var _ = cliente;
        var html = await cliente.GetStringAsync("/");
        Assert.Contains("Cerrar sesión", Texto(html));
        Assert.Contains("action=\"acceso/salir\"", html);

        using var salir = await cliente.PostAsync("/acceso/salir", content: null);
        using var despues = await cliente.GetAsync("/lista-maestra");

        Assert.Equal(HttpStatusCode.Redirect, salir.StatusCode);
        Assert.StartsWith("/acceso", Destino(despues));
    }

    [Fact]
    public async Task Mi_acceso_muestra_los_roles_asignados_y_no_habla_de_Active_Directory()
    {
        var (usuario, cliente) = await PersonaAsync(RolSistema.Revisor);
        using var _ = cliente;

        var texto = Texto(await cliente.GetStringAsync("/mi-acceso"));

        Assert.Contains(usuario, texto);
        Assert.Contains("Revisor", texto);
        Assert.Contains("los asigna un Administrador", texto);
        Assert.DoesNotContain("Active Directory", texto);
    }

    [Fact]
    public async Task El_acceso_de_desarrollo_no_existe_en_modo_Local()
    {
        using var cliente = app.CrearCliente();

        using var r = await cliente.PostAsync("/dev/login/admin", content: null);

        // No inicia sesión: no hay endpoint (o lo atiende la página «no encontrada»), y nada queda abierto.
        using var interior = await cliente.GetAsync("/lista-maestra");
        Assert.Equal(HttpStatusCode.Redirect, interior.StatusCode);
        Assert.StartsWith("/acceso", Destino(interior));
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
    }
}
