using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Correo;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Tests.Datos;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Correo;

/// <summary>El envío en sí: reintentos, concurrencia, arriendo y administración de la cola.</summary>
public class ColaCorreoTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static readonly TimeSpan CincoMin = TimeSpan.FromMinutes(5);

    /// <summary>Hace fallar solo los correos de este documento y cuenta cuántas veces se intentó.</summary>
    private static (Func<int> Intentos, Func<MensajeCorreo, Exception?> Falla) FallarPara(string codigo, Func<int, bool>? falla = null)
    {
        var intentos = 0;
        Exception? Falla(MensajeCorreo m)
        {
            if (!m.Asunto.Contains(codigo))
            {
                return null;
            }

            intentos++;
            return falla is null || falla(intentos) ? new InvalidOperationException("SMTP caído") : null;
        }

        return (() => intentos, Falla);
    }

    private static async Task<Notificacion> UnicaAsync(Escenario e, string codigo) =>
        Assert.Single(await e.NotificacionesDeAsync(codigo));

    // ---------- Envío ----------

    [Fact]
    public async Task Envia_a_la_direccion_de_la_convencion_y_marca_enviado()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.ProcesarColaAsync();

        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Enviado, n.Estado);
        Assert.Equal(1, n.Intentos);
        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime, n.FechaEnvioUtc);
        Assert.Null(n.UltimoError);
        var enviado = Assert.Single(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo));
        Assert.Equal($"{ResolutorCorreo.NombreDeUsuario(f.Revisor.Identificador).ToLowerInvariant()}@planta.test", enviado.Para);
        Assert.Equal(n.Destinatario!.NombreVisible, enviado.NombrePara);
        Assert.Equal(n.Asunto, enviado.Asunto);
        Assert.Equal(n.Cuerpo, enviado.Cuerpo);
    }

    [Fact]
    public async Task Un_correo_capturado_por_el_administrador_manda_sobre_la_convencion()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        await e.UsuariosAdmin.EstablecerCorreoAsync(f.Admin, f.RevisorId, "revisor.especial@otra.com");

        await e.ProcesarColaAsync();

        var enviado = Assert.Single(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo));
        Assert.Equal("revisor.especial@otra.com", enviado.Para);
    }

    [Fact]
    public async Task Una_notificacion_enviada_no_se_vuelve_a_enviar()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.ProcesarColaAsync();
        e.Reloj.Advance(TimeSpan.FromHours(2));
        await e.ProcesarColaAsync();

        Assert.Single(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo));
    }

    [Fact]
    public async Task El_tope_por_pasada_se_respeta()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f); // varias notificaciones (y posiblemente más de otras pruebas de la clase)

        var enviadas = await e.Cola.ProcesarPendientesAsync(maximo: 1);

        Assert.Equal(1, enviadas);
    }

    [Fact]
    public async Task Con_el_correo_desactivado_la_cola_no_hace_nada()
    {
        using var e = new Escenario(bd); // Modo Desactivado

        var enviadas = await e.ProcesarColaAsync();

        Assert.Equal(0, enviadas);
        Assert.Equal(0, e.CorreoSalida.Intentos);
    }

    // ---------- Fallos y reintentos ----------

    [Fact]
    public async Task Un_fallo_deja_la_notificacion_pendiente_con_espera_exponencial_hasta_agotar_intentos()
    {
        using var e = new Escenario(bd, correo: true); // MaximoIntentos 3, base 5 min
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var (intentos, falla) = FallarPara(f.Documento.Codigo);
        e.CorreoSalida.Falla = falla;
        var t0 = e.Reloj.GetUtcNow().UtcDateTime;

        await e.ProcesarColaAsync();                                   // intento 1
        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Pendiente, n.Estado);
        Assert.Equal(1, n.Intentos);
        Assert.Equal(t0.AddMinutes(5), n.ProximoIntentoUtc);
        Assert.Contains("SMTP caído", n.UltimoError);

        e.Reloj.Advance(CincoMin - TimeSpan.FromMinutes(1));           // todavía no toca
        await e.ProcesarColaAsync();
        Assert.Equal(1, intentos());

        e.Reloj.Advance(TimeSpan.FromMinutes(1));                      // toca: intento 2, espera de 10 min
        await e.ProcesarColaAsync();
        n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(2, n.Intentos);
        Assert.Equal(EstadoNotificacion.Pendiente, n.Estado);
        Assert.Equal(e.Reloj.GetUtcNow().UtcDateTime.AddMinutes(10), n.ProximoIntentoUtc);

        e.Reloj.Advance(TimeSpan.FromMinutes(10));                     // intento 3 = el último
        await e.ProcesarColaAsync();
        n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Error, n.Estado);
        Assert.Equal(3, n.Intentos);

        e.Reloj.Advance(TimeSpan.FromDays(1));                         // en Error ya no se insiste sola
        await e.ProcesarColaAsync();
        Assert.Equal(3, intentos());
    }

    [Fact]
    public async Task Si_el_servidor_se_recupera_el_reintento_lo_entrega()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var (_, falla) = FallarPara(f.Documento.Codigo, intento => intento == 1); // solo el primero falla
        e.CorreoSalida.Falla = falla;

        await e.ProcesarColaAsync();
        e.Reloj.Advance(CincoMin);
        await e.ProcesarColaAsync();

        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Enviado, n.Estado);
        Assert.Equal(2, n.Intentos);
        Assert.Null(n.UltimoError);
        Assert.Single(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo));
    }

    [Fact]
    public async Task Un_fallo_en_una_notificacion_no_impide_enviar_las_demas()
    {
        using var e = new Escenario(bd, correo: true);
        var mala = await e.PrepararFlujoAsync();
        var buena = await e.PrepararFlujoAsync();
        await e.IniciarAsync(mala);
        await e.IniciarAsync(buena);
        e.CorreoSalida.Falla = FallarPara(mala.Documento.Codigo).Falla;

        await e.ProcesarColaAsync();

        Assert.Equal(EstadoNotificacion.Pendiente, (await UnicaAsync(e, mala.Documento.Codigo)).Estado);
        Assert.Equal(EstadoNotificacion.Enviado, (await UnicaAsync(e, buena.Documento.Codigo)).Estado);
    }

    [Fact]
    public async Task Sin_correo_del_destinatario_queda_el_motivo_y_al_capturarlo_se_entrega()
    {
        // Sin convención configurada y sin correo capturado, no hay a dónde mandar.
        using var e = new Escenario(bd, correo: true, extra: new Dictionary<string, string?> { ["Correo:PatronCorreo"] = "" });
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);

        await e.ProcesarColaAsync();

        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Pendiente, n.Estado);
        Assert.Contains("no tiene correo", n.UltimoError);
        Assert.DoesNotContain(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo));

        await e.UsuariosAdmin.EstablecerCorreoAsync(f.Admin, f.RevisorId, "revisor@planta.test");
        e.Reloj.Advance(CincoMin);
        await e.ProcesarColaAsync();

        Assert.Equal(EstadoNotificacion.Enviado, (await UnicaAsync(e, f.Documento.Codigo)).Estado);
        Assert.Equal("revisor@planta.test", Assert.Single(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo)).Para);
    }

    // ---------- Concurrencia y arriendo ----------

    [Fact]
    public async Task Varios_procesadores_a_la_vez_envian_cada_mensaje_una_sola_vez()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.PublicarPrimeraAsync(f);
        var (doc2, _) = await e.PublicarOtroAsync(f, "Segundo", "otro");
        var (doc3, _) = await e.PublicarOtroAsync(f, "Tercero", "otro mas");
        var codigos = new[] { f.Documento.Codigo, doc2.Codigo, doc3.Codigo };

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => e.ProcesarColaAsync())));

        foreach (var codigo in codigos)
        {
            var notificaciones = await e.NotificacionesDeAsync(codigo);
            Assert.NotEmpty(notificaciones);
            Assert.All(notificaciones, n => Assert.Equal(EstadoNotificacion.Enviado, n.Estado));
            Assert.All(notificaciones, n => Assert.Equal(1, n.Intentos));
            Assert.Equal(notificaciones.Count, e.CorreoSalida.Enviados.Count(m => m.Asunto.Contains(codigo)));
        }
    }

    [Fact]
    public async Task Una_notificacion_atorada_en_procesando_se_recupera_al_vencer_el_arriendo()
    {
        using var e = new Escenario(bd, correo: true); // arriendo de 10 minutos
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var ahora = e.Reloj.GetUtcNow().UtcDateTime;
        await using (var db = e.Db()) // un proceso la tomó y murió antes de terminar
        {
            await db.Notificaciones.Where(n => n.CodigoDocumento == f.Documento.Codigo).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Estado, EstadoNotificacion.Procesando)
                .SetProperty(n => n.Intentos, 1)
                .SetProperty(n => n.ProximoIntentoUtc, ahora.AddMinutes(10)));
        }

        await e.ProcesarColaAsync();
        Assert.DoesNotContain(e.CorreoSalida.Enviados, m => m.Asunto.Contains(f.Documento.Codigo)); // aún hay alguien trabajando en ella

        e.Reloj.Advance(TimeSpan.FromMinutes(11));
        await e.ProcesarColaAsync();

        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Enviado, n.Estado);
        Assert.Equal(2, n.Intentos);
    }

    // ---------- Administración ----------

    private static async Task<(EscenarioFlujo F, long Id)> EnErrorAsync(Escenario e)
    {
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        e.CorreoSalida.Falla = FallarPara(f.Documento.Codigo).Falla;
        for (var i = 0; i < 3; i++)
        {
            await e.ProcesarColaAsync();
            e.Reloj.Advance(TimeSpan.FromMinutes(20));
        }

        var n = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Error, n.Estado);
        return (f, n.Id);
    }

    [Fact]
    public async Task Reintentar_una_notificacion_en_error_la_reenvia_y_queda_en_la_bitacora()
    {
        using var e = new Escenario(bd, correo: true);
        var (f, id) = await EnErrorAsync(e);
        e.CorreoSalida.Falla = null; // el servidor ya funciona

        await e.Cola.ReintentarAsync(f.Admin, id);
        var tras = await UnicaAsync(e, f.Documento.Codigo);
        Assert.Equal(EstadoNotificacion.Pendiente, tras.Estado);
        Assert.Equal(0, tras.Intentos);

        await e.ProcesarColaAsync();

        Assert.Equal(EstadoNotificacion.Enviado, (await UnicaAsync(e, f.Documento.Codigo)).Estado);
        var evento = Assert.Single(await e.AuditoriaDeAsync(id.ToString()), a => a.Accion == "Correo.Reintentar");
        Assert.Equal(f.Admin.Identificador, evento.Usuario);
    }

    [Fact]
    public async Task Solo_se_reintentan_las_notificaciones_en_error()
    {
        using var e = new Escenario(bd, correo: true);
        var f = await e.PrepararFlujoAsync();
        await e.IniciarAsync(f);
        var n = await UnicaAsync(e, f.Documento.Codigo); // Pendiente

        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Cola.ReintentarAsync(f.Admin, n.Id));
        await Assert.ThrowsAsync<ReglaNegocioException>(() => e.Cola.ReintentarAsync(f.Admin, long.MaxValue));
    }

    [Fact]
    public async Task Solo_el_administrador_ve_y_reintenta_la_cola()
    {
        using var e = new Escenario(bd, correo: true);
        var (f, id) = await EnErrorAsync(e);

        foreach (var actor in new[] { f.Autor, f.Revisor, f.Aprobador, await e.NuevoActorAsync([RolSistema.Auditor]) })
        {
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Cola.ListarAsync(actor, null));
            await Assert.ThrowsAsync<AccesoDenegadoException>(() => e.Cola.ReintentarAsync(actor, id));
        }

        Assert.Equal(EstadoNotificacion.Error, (await UnicaAsync(e, f.Documento.Codigo)).Estado); // nada cambió
    }

    [Fact]
    public async Task El_listado_filtra_por_estado_y_muestra_el_correo_efectivo_y_el_ultimo_error()
    {
        using var e = new Escenario(bd, correo: true);
        var (f, id) = await EnErrorAsync(e);

        var enError = await e.Cola.ListarAsync(f.Admin, EstadoNotificacion.Error, pagina: 1, tamano: 500);
        var fila = Assert.Single(enError.Elementos, x => x.Id == id);

        Assert.All(enError.Elementos, x => Assert.Equal(EstadoNotificacion.Error, x.Estado));
        Assert.Equal($"{ResolutorCorreo.NombreDeUsuario(f.Revisor.Identificador).ToLowerInvariant()}@planta.test", fila.Correo);
        Assert.Contains("SMTP caído", fila.UltimoError);
        Assert.Equal(f.Documento.Codigo, fila.CodigoDocumento);
        Assert.Equal(3, fila.Intentos);

        var enviadas = await e.Cola.ListarAsync(f.Admin, EstadoNotificacion.Enviado, pagina: 1, tamano: 500);
        Assert.DoesNotContain(enviadas.Elementos, x => x.Id == id);
    }
}
