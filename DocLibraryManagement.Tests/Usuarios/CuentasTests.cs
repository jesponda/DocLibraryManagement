using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocLibraryManagement.Tests.Usuarios;

/// <summary>
/// La lista de usuarios con roles (modo de acceso «Local»): alta, edición, contraseñas temporales, bloqueo por intentos y las
/// protecciones para que nadie se quede sin Administrador.
/// </summary>
public class CuentasTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static string Id() => $"u{Guid.NewGuid():N}"[..12];

    private static DatosCuenta Datos(string nombre = "Persona de prueba", string? correo = null, RolSistema[]? roles = null, string[]? areas = null) =>
        new(nombre, correo, roles ?? [], areas ?? []);

    /// <summary>Un administrador «de la lista» (con cuenta real), como el que usaría la pantalla.</summary>
    private static async Task<(Escenario E, ICuentasService Cuentas, IAutenticacionLocal Auth, Actor Admin)> PrepararAsync(BaseDatosFixture bd)
    {
        var e = new Escenario(bd);
        var cuentas = e.Scope().GetRequiredService<ICuentasService>();
        var auth = e.Scope().GetRequiredService<IAutenticacionLocal>();
        var admin = await e.NuevoAdminAsync();
        return (e, cuentas, auth, admin);
    }

    // ---------- alta ----------

    [Fact]
    public async Task Dar_de_alta_crea_la_cuenta_con_contrasena_temporal_que_permite_entrar_y_obliga_a_cambiarla()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();

        var alta = await cuentas.CrearAsync(admin, id, Datos("Ana Pérez", "ana@planta.test", [RolSistema.Revisor]));

        Assert.Equal(id, alta.Identificador);
        Assert.Matches(@"^[A-Za-z0-9]{4}-[A-Za-z0-9]{4}-[A-Za-z0-9]{4}$", alta.ContrasenaTemporal);
        var cuenta = (await cuentas.ListarAsync(admin)).Single(c => c.Id == alta.Id);
        Assert.True(cuenta.Activa);
        Assert.True(cuenta.DebeCambiarContrasena);
        Assert.Equal([RolSistema.Revisor], cuenta.Roles);
        Assert.Equal("ana@planta.test", cuenta.Correo);

        var entrada = await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, "10.0.0.5");
        Assert.Equal(ResultadoInicioSesion.Correcto, entrada.Resultado);
        Assert.True(entrada.Cuenta!.DebeCambiarContrasena);
        Assert.Contains(RolSistema.Revisor, entrada.Cuenta.Roles);
        Assert.Contains(RolSistema.Lector, entrada.Cuenta.Roles); // Lector es implícito
    }

    [Fact]
    public async Task La_contrasena_nunca_se_guarda_en_claro_ni_se_escribe_en_la_bitacora()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();

        var alta = await cuentas.CrearAsync(admin, id, Datos());

        await using var db = bd.Crear();
        var usuario = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Identificador == id);
        Assert.StartsWith("PBKDF2$", usuario.HashContrasena);
        Assert.DoesNotContain(alta.ContrasenaTemporal, usuario.HashContrasena);
        var eventos = await db.Auditoria.AsNoTracking().Where(a => a.EntidadId == id).ToListAsync();
        Assert.Contains(eventos, ev => ev.Accion == "Usuario.Crear" && ev.Usuario == admin.Identificador);
        Assert.DoesNotContain(eventos, ev => ($"{ev.Comentario}{ev.ValorNuevo}{ev.ValorAnterior}").Contains(alta.ContrasenaTemporal));
    }

    [Fact]
    public async Task Dos_cuentas_no_comparten_la_misma_contrasena_temporal_ni_el_mismo_hash()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;

        var a = await cuentas.CrearAsync(admin, Id(), Datos());
        var b = await cuentas.CrearAsync(admin, Id(), Datos());

        Assert.NotEqual(a.ContrasenaTemporal, b.ContrasenaTemporal);
    }

    [Theory]
    [InlineData("ab")]            // muy corto
    [InlineData("Ana Perez")]     // espacio y mayúsculas
    [InlineData("DOM\\ana")]      // identificador de dominio
    [InlineData("ana@planta.com")] // arroba
    [InlineData("")]
    public async Task Un_nombre_de_usuario_invalido_se_rechaza(string identificador)
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, identificador, Datos()));
    }

    [Fact]
    public async Task El_nombre_de_usuario_se_normaliza_y_no_se_repite_aunque_cambien_las_mayusculas()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();

        var alta = await cuentas.CrearAsync(admin, $"  {id.ToUpperInvariant()} ", Datos());

        Assert.Equal(id, alta.Identificador);
        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, id, Datos()));
        Assert.Contains("Ya existe", ex.Message);
        // y al entrar tampoco importan las mayúsculas del usuario
        Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id.ToUpperInvariant(), alta.ContrasenaTemporal, null)).Resultado);
    }

    [Fact]
    public async Task Un_autor_necesita_areas_y_estas_deben_existir()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;
        var (_, codigoArea, _, _) = await e.NuevaAreaConTipoAsync(admin);

        var sinAreas = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, Id(), Datos(roles: [RolSistema.Autor])));
        var inexistente = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, Id(), Datos(roles: [RolSistema.Autor], areas: ["NOEXISTE"])));
        var alta = await cuentas.CrearAsync(admin, Id(), Datos(roles: [RolSistema.Autor], areas: [codigoArea.ToLowerInvariant()]));

        Assert.Contains("al menos un área", sinAreas.Message);
        Assert.Contains("NOEXISTE", inexistente.Message);
        var cuenta = (await cuentas.ListarAsync(admin)).Single(c => c.Id == alta.Id);
        Assert.Equal([codigoArea], cuenta.Areas); // se guarda el código tal como está en el catálogo
    }

    [Fact]
    public async Task Las_areas_solo_cuentan_para_el_rol_Autor()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var (_, codigoArea, _, _) = await e.NuevaAreaConTipoAsync(admin);
        var id = Id();

        var alta = await cuentas.CrearAsync(admin, id, Datos(roles: [RolSistema.Revisor], areas: [codigoArea]));

        var acceso = await auth.ObtenerAccesoAsync(id);
        Assert.Empty(acceso!.Areas);
        Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
    }

    [Fact]
    public async Task El_correo_se_valida_y_el_nombre_es_obligatorio()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;

        await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, Id(), Datos(correo: "no-es-correo")));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CrearAsync(admin, Id(), Datos(nombre: "   ")));
    }

    [Fact]
    public async Task Solo_un_Administrador_gestiona_las_cuentas()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;
        var alta = await cuentas.CrearAsync(admin, Id(), Datos());
        var autor = await e.NuevoAutorAsync("XX");
        var auditor = await e.NuevoActorAsync([RolSistema.Auditor]);

        foreach (var actor in new[] { autor, auditor })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => cuentas.ListarAsync(actor));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => cuentas.CrearAsync(actor, Id(), Datos()));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => cuentas.ActualizarAsync(actor, alta.Id, Datos()));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => cuentas.RestablecerContrasenaAsync(actor, alta.Id));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => cuentas.CambiarEstadoAsync(actor, alta.Id, false));
        }
    }

    // ---------- edición ----------

    [Fact]
    public async Task Editar_cambia_roles_y_areas_y_la_persona_aparece_como_candidata_en_los_flujos()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());

        await cuentas.ActualizarAsync(admin, alta.Id, Datos("Nombre nuevo", null, [RolSistema.Aprobador, RolSistema.Revisor]));

        var acceso = await auth.ObtenerAccesoAsync(id);
        Assert.Contains(RolSistema.Aprobador, acceso!.Roles);
        Assert.Contains(RolSistema.Revisor, acceso.Roles);
        await using var db = bd.Crear();
        var usuario = await db.Usuarios.AsNoTracking().SingleAsync(u => u.Identificador == id);
        Assert.Equal("Nombre nuevo", usuario.NombreVisible);
        Assert.True(usuario.TuvoRolEnUltimoAcceso(RolSistema.Aprobador)); // las listas de candidatos del flujo la ven desde el alta
        Assert.Contains(await db.Auditoria.AsNoTracking().ToListAsync(), ev => ev.Accion == "Usuario.Actualizar" && ev.EntidadId == id);
    }

    [Fact]
    public async Task Los_cambios_de_rol_surten_efecto_en_la_siguiente_lectura_sin_volver_a_entrar()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos(roles: [RolSistema.Revisor]));

        await cuentas.ActualizarAsync(admin, alta.Id, Datos());

        Assert.DoesNotContain(RolSistema.Revisor, (await auth.ObtenerAccesoAsync(id))!.Roles);
    }

    [Fact]
    public async Task Nadie_puede_quitarse_a_si_mismo_el_rol_de_Administrador_ni_desactivarse()
    {
        var (e, cuentas, _, admin) = await PrepararAsync(bd);
        using var _ = e;
        // el administrador de la prueba debe existir como cuenta de la lista
        var yo = await cuentas.CrearAsync(admin, Id(), Datos("Admin real", null, [RolSistema.Administrador]));
        var actor = new Actor(yo.Identificador, new HashSet<RolSistema> { RolSistema.Administrador, RolSistema.Lector }, new HashSet<string>());
        await cuentas.CrearAsync(actor, Id(), Datos("Otro admin", null, [RolSistema.Administrador])); // hay otro: no es el «último»

        var quitarRol = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.ActualizarAsync(actor, yo.Id, Datos("Admin real")));
        var desactivar = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CambiarEstadoAsync(actor, yo.Id, false));
        var restablecer = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.RestablecerContrasenaAsync(actor, yo.Id));

        Assert.Contains("a ti mismo", quitarRol.Message);
        Assert.Contains("propia cuenta", desactivar.Message);
        Assert.Contains("Cambiar contraseña", restablecer.Message);
    }

    // ---------- estado ----------

    [Fact]
    public async Task Una_cuenta_desactivada_no_puede_entrar_y_al_reactivarla_si()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        var selloAntes = (await auth.ObtenerAccesoAsync(id))!.Sello;

        await cuentas.CambiarEstadoAsync(admin, alta.Id, false);

        Assert.Equal(ResultadoInicioSesion.Rechazado, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        var desactivada = (await auth.ObtenerAccesoAsync(id))!;
        Assert.False(desactivada.Activa);
        Assert.NotEqual(selloAntes, desactivada.Sello); // las sesiones abiertas dejan de valer

        await cuentas.CambiarEstadoAsync(admin, alta.Id, true);
        Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
    }

    // ---------- inicio de sesión ----------

    [Theory]
    [InlineData("usuario-que-no-existe", "cualquiera-123456")]
    [InlineData("", "")]
    public async Task Un_usuario_inexistente_se_rechaza_igual_que_una_contrasena_mala(string usuario, string contrasena)
    {
        var (e, _, auth, _) = await PrepararAsync(bd);
        using var _ = e;

        var r = await auth.IniciarSesionAsync(usuario, contrasena, null);

        Assert.Equal(ResultadoInicioSesion.Rechazado, r.Resultado);
        Assert.Null(r.Cuenta);
    }

    [Fact]
    public async Task Una_cuenta_sin_contrasena_por_ejemplo_la_de_un_acceso_anterior_no_puede_entrar()
    {
        var (e, _, auth, _) = await PrepararAsync(bd);
        using var _ = e;
        var actor = await e.NuevoAutorAsync("XX"); // existe en Usuarios pero nunca tuvo contraseña

        var r = await auth.IniciarSesionAsync(actor.Identificador, "", null);

        Assert.Equal(ResultadoInicioSesion.Rechazado, r.Resultado);
    }

    [Fact]
    public async Task Cinco_intentos_fallidos_bloquean_la_cuenta_incluso_con_la_contrasena_correcta()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());

        for (var i = 1; i <= 4; i++)
        {
            Assert.Equal(ResultadoInicioSesion.Rechazado, (await auth.IniciarSesionAsync(id, "mala-contrasena", null)).Resultado);
        }

        Assert.Equal(ResultadoInicioSesion.Bloqueado, (await auth.IniciarSesionAsync(id, "mala-contrasena", null)).Resultado);
        Assert.Equal(ResultadoInicioSesion.Bloqueado, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        Assert.NotNull((await cuentas.ListarAsync(admin)).Single(c => c.Id == alta.Id).BloqueadaHastaUtc);
        await using var db = bd.Crear();
        Assert.Contains(await db.Auditoria.AsNoTracking().ToListAsync(), ev => ev.Accion == "Sesion.Bloqueo" && ev.EntidadId == id);
    }

    [Fact]
    public async Task El_bloqueo_termina_solo_con_el_tiempo_y_un_acierto_reinicia_la_cuenta()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        for (var i = 0; i < 5; i++)
        {
            await auth.IniciarSesionAsync(id, "mala-contrasena", null);
        }

        e.Reloj.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(ResultadoInicioSesion.Bloqueado, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        e.Reloj.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        Assert.Null((await cuentas.ListarAsync(admin)).Single(c => c.Id == alta.Id).BloqueadaHastaUtc);
    }

    [Fact]
    public async Task Los_fallos_sueltos_no_bloquean_si_hay_un_acierto_entre_ellos()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());

        for (var ronda = 0; ronda < 3; ronda++)
        {
            for (var i = 0; i < 4; i++)
            {
                await auth.IniciarSesionAsync(id, "mala-contrasena", null);
            }

            Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        }
    }

    [Fact]
    public async Task Restablecer_genera_otra_contrasena_desbloquea_y_cierra_las_sesiones_abiertas()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        var sello = (await auth.ObtenerAccesoAsync(id))!.Sello;
        for (var i = 0; i < 5; i++)
        {
            await auth.IniciarSesionAsync(id, "mala-contrasena", null);
        }

        var nueva = await cuentas.RestablecerContrasenaAsync(admin, alta.Id);

        Assert.NotEqual(alta.ContrasenaTemporal, nueva);
        Assert.Equal(ResultadoInicioSesion.Rechazado, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        var entrada = await auth.IniciarSesionAsync(id, nueva, null);
        Assert.Equal(ResultadoInicioSesion.Correcto, entrada.Resultado);
        Assert.True(entrada.Cuenta!.DebeCambiarContrasena);
        Assert.NotEqual(sello, entrada.Cuenta.Sello);
    }

    // ---------- cambio de contraseña ----------

    [Fact]
    public async Task Cambiar_la_contrasena_quita_la_obligacion_y_cierra_las_demas_sesiones()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        var sello = (await auth.ObtenerAccesoAsync(id))!.Sello;

        var acceso = await auth.CambiarContrasenaAsync(id, alta.ContrasenaTemporal, "Mi-clave-segura-2026", null);

        Assert.False(acceso.DebeCambiarContrasena);
        Assert.NotEqual(sello, acceso.Sello);
        Assert.Equal(ResultadoInicioSesion.Rechazado, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
        var entrada = await auth.IniciarSesionAsync(id, "Mi-clave-segura-2026", null);
        Assert.Equal(ResultadoInicioSesion.Correcto, entrada.Resultado);
        Assert.False(entrada.Cuenta!.DebeCambiarContrasena);
    }

    [Theory]
    [InlineData("corta1")]                // menos de 10
    [InlineData("aaaaaaaaaaaaaaaa")]      // sin variedad
    [InlineData("conELusuarioXX-123")]    // contiene el usuario (se arma abajo)
    public async Task Una_contrasena_debil_se_rechaza_y_la_actual_sigue_valiendo(string candidata)
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        candidata = candidata.Replace("conELusuarioXX", $"con{id}");

        await Assert.ThrowsAsync<ReglaNegocioException>(() => auth.CambiarContrasenaAsync(id, alta.ContrasenaTemporal, candidata, null));

        Assert.Equal(ResultadoInicioSesion.Correcto, (await auth.IniciarSesionAsync(id, alta.ContrasenaTemporal, null)).Resultado);
    }

    [Fact]
    public async Task Para_cambiar_la_contrasena_hay_que_dar_la_actual_y_los_errores_cuentan_para_el_bloqueo()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());

        for (var i = 0; i < 4; i++)
        {
            var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => auth.CambiarContrasenaAsync(id, "no-es-la-actual", "Otra-clave-segura-1", null));
            Assert.Contains("actual no es correcta", ex.Message);
        }

        await Assert.ThrowsAsync<ReglaNegocioException>(() => auth.CambiarContrasenaAsync(id, "no-es-la-actual", "Otra-clave-segura-1", null));
        // ya bloqueada: ni siquiera con la actual correcta
        await Assert.ThrowsAsync<ReglaNegocioException>(() => auth.CambiarContrasenaAsync(id, alta.ContrasenaTemporal, "Otra-clave-segura-1", null));
    }

    [Fact]
    public async Task La_contrasena_nueva_debe_ser_distinta_de_la_actual()
    {
        var (e, cuentas, auth, admin) = await PrepararAsync(bd);
        using var _ = e;
        var id = Id();
        var alta = await cuentas.CrearAsync(admin, id, Datos());
        await auth.CambiarContrasenaAsync(id, alta.ContrasenaTemporal, "Clave-segura-uno-1", null);

        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => auth.CambiarContrasenaAsync(id, "Clave-segura-uno-1", "Clave-segura-uno-1", null));

        Assert.Contains("distinta", ex.Message);
    }

    [Fact]
    public void Las_reglas_de_contrasena_dan_mensajes_claros()
    {
        Assert.Null(ReglasContrasena.Validar("Una-clave-buena-9", "jperez"));
        Assert.Contains("al menos 10", ReglasContrasena.Validar("corta", "jperez")!);
        Assert.Contains("simple", ReglasContrasena.Validar("1111111111111", "jperez")!);
        Assert.Contains("nombre de usuario", ReglasContrasena.Validar("xxJPEREZxx-2026", "jperez")!);
        Assert.NotNull(ReglasContrasena.Validar(null, "jperez"));
        Assert.NotNull(ReglasContrasena.Validar(new string('a', 129) + "bcd1", "jperez"));
    }
}

/// <summary>El Administrador inicial: se crea una sola vez, cuando todavía no hay cuentas con contraseña.</summary>
public class AdministradorInicialTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Fact]
    public async Task Se_crea_una_sola_vez_con_contrasena_temporal_y_rol_de_Administrador()
    {
        using var e = new Escenario(bd);
        var auth = e.Scope().GetRequiredService<IAutenticacionLocal>();

        var primera = await auth.AsegurarAdministradorInicialAsync("admin", "Administrador", "Inicial-Temporal-1");
        var segunda = await auth.AsegurarAdministradorInicialAsync("otro-admin", "Otro", null);

        Assert.Equal("Inicial-Temporal-1", primera);
        Assert.Null(segunda);                                                   // ya existe una cuenta con contraseña
        Assert.Null(await auth.ObtenerAccesoAsync("otro-admin"));
        var entrada = await auth.IniciarSesionAsync("admin", "Inicial-Temporal-1", null);
        Assert.Equal(ResultadoInicioSesion.Correcto, entrada.Resultado);
        Assert.True(entrada.Cuenta!.DebeCambiarContrasena);
        Assert.Contains(RolSistema.Administrador, entrada.Cuenta.Roles);
    }
}

/// <summary>Con su propia base de datos: la regla cuenta a todos los Administradores activos, y en una base compartida habría más.</summary>
public class UltimoAdministradorTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Fact]
    public async Task No_se_puede_dejar_al_sistema_sin_Administrador_activo()
    {
        using var e = new Escenario(bd);
        var cuentas = e.Scope().GetRequiredService<ICuentasService>();
        var auth = e.Scope().GetRequiredService<IAutenticacionLocal>();
        await auth.AsegurarAdministradorInicialAsync("primero", "Primero", "Inicial-Temporal-1");
        var primero = new Actor("primero", new HashSet<RolSistema> { RolSistema.Administrador }, new HashSet<string>());
        var alta = await cuentas.CrearAsync(primero, "segundo", new DatosCuenta("Segundo", null, [RolSistema.Administrador], []));
        var segundo = new Actor("segundo", new HashSet<RolSistema> { RolSistema.Administrador }, new HashSet<string>());
        var idPrimero = (await cuentas.ListarAsync(primero)).Single(c => c.Identificador == "primero").Id;

        // Con dos administradores se puede desactivar a uno…
        await cuentas.CambiarEstadoAsync(segundo, idPrimero, false);

        // …pero el que queda es el último: no puede perder el rol ni desactivarse (otro lo haría), y nadie más puede quitárselo.
        var yoMismo = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.ActualizarAsync(segundo, alta.Id, new DatosCuenta("Segundo", null, [], [])));
        Assert.Contains("a ti mismo", yoMismo.Message);
        var tercero = await e.NuevoAdminAsync(); // administrador «de paso» (sin cuenta con contraseña): no cuenta como respaldo
        var ex = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.CambiarEstadoAsync(tercero, alta.Id, false));
        Assert.Contains("al menos un Administrador", ex.Message);
        var ex2 = await Assert.ThrowsAsync<ReglaNegocioException>(() => cuentas.ActualizarAsync(tercero, alta.Id, new DatosCuenta("Segundo", null, [], [])));
        Assert.Contains("al menos un Administrador", ex2.Message);
    }
}
