using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;

namespace DocLibraryManagement.Tests.Correo;

/// <summary>Administración de usuarios y sus correos.</summary>
public class UsuariosServiceTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Fact]
    public async Task El_listado_muestra_el_correo_efectivo_y_de_donde_sale()
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();
        var conCorreo = await e.NuevoAutorAsync();
        var sinCorreo = await e.NuevoAutorAsync();
        var idConCorreo = await e.IdDeAsync(conCorreo);
        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, idConCorreo, "  especial@otra.com  ");

        var lista = await e.UsuariosAdmin.ListarAsync(admin);

        var a = Assert.Single(lista, u => u.Id == idConCorreo);
        Assert.Equal("especial@otra.com", a.CorreoExplicito);       // se guardó sin espacios
        Assert.Equal("especial@otra.com", a.CorreoEfectivo);
        Assert.Equal(OrigenCorreo.Explicito, a.Origen);
        var b = Assert.Single(lista, u => u.Identificador == sinCorreo.Identificador);
        Assert.Null(b.CorreoExplicito);
        Assert.Equal($"{ResolutorCorreo.NombreDeUsuario(sinCorreo.Identificador).ToLowerInvariant()}@planta.test", b.CorreoEfectivo);
        Assert.Equal(OrigenCorreo.Convencion, b.Origen);
        Assert.Contains("Autor", b.Roles);
    }

    [Fact]
    public async Task Quitar_el_correo_vuelve_a_la_convencion()
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();
        var persona = await e.NuevoAutorAsync();
        var id = await e.IdDeAsync(persona);
        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, "especial@otra.com");

        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, "   ");

        var u = Assert.Single(await e.UsuariosAdmin.ListarAsync(admin), x => x.Id == id);
        Assert.Null(u.CorreoExplicito);
        Assert.Equal(OrigenCorreo.Convencion, u.Origen);
    }

    [Fact]
    public async Task Sin_convencion_ni_correo_el_listado_lo_marca_como_sin_correo()
    {
        using var e = new Escenario(bd, correo: true, extra: new Dictionary<string, string?> { ["Correo:PatronCorreo"] = "" });
        var admin = await e.NuevoAdminAsync();
        var persona = await e.NuevoAutorAsync();

        var u = Assert.Single(await e.UsuariosAdmin.ListarAsync(admin), x => x.Identificador == persona.Identificador);

        Assert.Null(u.CorreoEfectivo);
        Assert.Equal(OrigenCorreo.SinCorreo, u.Origen);
    }

    [Theory]
    [InlineData("esto no es un correo")]
    [InlineData("juan@localhost")]
    [InlineData("Juan <juan@empresa.com>")]
    [InlineData("juan@empresa.com\r\nBcc: espia@x.com")]
    public async Task Un_correo_invalido_se_rechaza_y_no_cambia_nada(string invalido)
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();
        var persona = await e.NuevoAutorAsync();
        var id = await e.IdDeAsync(persona);
        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, "bueno@empresa.com");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, invalido));

        Assert.Equal("bueno@empresa.com", Assert.Single(await e.UsuariosAdmin.ListarAsync(admin), x => x.Id == id).CorreoExplicito);
    }

    [Fact]
    public async Task Un_usuario_inexistente_se_rechaza()
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.UsuariosAdmin.EstablecerCorreoAsync(admin, Guid.NewGuid(), "a@empresa.com"));
    }

    [Fact]
    public async Task Cambiar_el_correo_queda_en_la_bitacora_con_el_valor_anterior_y_el_nuevo()
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();
        var persona = await e.NuevoAutorAsync();
        var id = await e.IdDeAsync(persona);

        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, "uno@empresa.com");
        await e.UsuariosAdmin.EstablecerCorreoAsync(admin, id, "dos@empresa.com");

        var eventos = (await e.AuditoriaDeAsync(persona.Identificador)).Where(a => a.Accion == "Usuario.EstablecerCorreo").ToList();
        Assert.Equal(2, eventos.Count);
        Assert.All(eventos, a => Assert.Equal(admin.Identificador, a.Usuario));
        Assert.Equal("uno@empresa.com", eventos[1].ValorAnterior);
        Assert.Equal("dos@empresa.com", eventos[1].ValorNuevo);
    }

    [Fact]
    public async Task Solo_el_administrador_gestiona_los_usuarios()
    {
        using var e = new Escenario(bd, correo: true);
        var admin = await e.NuevoAdminAsync();
        var persona = await e.NuevoAutorAsync();
        var id = await e.IdDeAsync(persona);

        foreach (var actor in new[] { persona, await e.NuevoActorAsync([RolSistema.Auditor]), await e.NuevoAprobadorAsync() })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.UsuariosAdmin.ListarAsync(actor));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.UsuariosAdmin.EstablecerCorreoAsync(actor, id, "a@empresa.com"));
        }

        Assert.Null(Assert.Single(await e.UsuariosAdmin.ListarAsync(admin), x => x.Id == id).CorreoExplicito);
    }
}
