using DocLibraryManagement.Application.Usuarios;
using DocLibraryManagement.Domain;
using DocLibraryManagement.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DocLibraryManagement.Tests.Datos;

public class AuditoriaYUsuariosTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static EventoAuditoria NuevoEvento(string accion = "Prueba") => new()
    {
        FechaUtc = DateTime.UtcNow,
        Usuario = "TEST\\auditor",
        Accion = accion,
        Entidad = "Documento",
        EntidadId = "1",
    };

    [Fact]
    public async Task La_bitacora_admite_inserciones()
    {
        await using var db = bd.Crear();
        var evento = NuevoEvento();
        db.Auditoria.Add(evento);

        await db.SaveChangesAsync();

        Assert.True(evento.Id > 0);
    }

    [Fact]
    public async Task La_bitacora_no_se_puede_modificar()
    {
        await using var db = bd.Crear();
        var evento = NuevoEvento();
        db.Auditoria.Add(evento);
        await db.SaveChangesAsync();

        evento.Accion = "Alterada";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var otra = bd.Crear();
        Assert.Equal("Prueba", (await otra.Auditoria.FirstAsync(e => e.Id == evento.Id)).Accion);
    }

    [Fact]
    public async Task La_bitacora_no_se_puede_eliminar()
    {
        await using var db = bd.Crear();
        var evento = NuevoEvento();
        db.Auditoria.Add(evento);
        await db.SaveChangesAsync();

        db.Auditoria.Remove(evento);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var otra = bd.Crear();
        Assert.True(await otra.Auditoria.AnyAsync(e => e.Id == evento.Id));
    }

    [Fact]
    public async Task Un_cambio_en_la_bitacora_no_arrastra_otros_cambios_pendientes()
    {
        await using var db = bd.Crear();
        var evento = NuevoEvento();
        db.Auditoria.Add(evento);
        await db.SaveChangesAsync();

        evento.Comentario = "Alterado";
        db.Areas.Add(new Area { Codigo = "ARRASTRE", Nombre = "No debe guardarse" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        await using var otra = bd.Crear();
        Assert.False(await otra.Areas.AnyAsync(a => a.Codigo == "ARRASTRE"));
    }

    private IUsuarioSincronizador CrearSincronizador(out ServiceProvider sp, FakeTimeProvider reloj)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = bd.Conexion })
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(config);
        services.AddSingleton<TimeProvider>(reloj);
        sp = services.BuildServiceProvider();
        return sp.CreateScope().ServiceProvider.GetRequiredService<IUsuarioSincronizador>();
    }

    [Fact]
    public async Task El_primer_acceso_crea_al_usuario_y_los_siguientes_lo_actualizan()
    {
        var id = $"TEST\\{Guid.NewGuid():N}";
        var reloj = new FakeTimeProvider(new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero));
        var sincronizador = CrearSincronizador(out var sp, reloj);
        using var _ = sp;

        var primero = await sincronizador.SincronizarAsync(id, "Nombre inicial", "a@planta.com");
        reloj.Advance(TimeSpan.FromHours(2));
        var segundo = await sincronizador.SincronizarAsync(id, "Nombre nuevo", null);

        Assert.Equal(primero.Id, segundo.Id);
        await using var db = bd.Crear();
        var guardado = await db.Usuarios.SingleAsync(u => u.Identificador == id);
        Assert.Equal("Nombre nuevo", guardado.NombreVisible);
        Assert.Equal("a@planta.com", guardado.Correo); // un correo nulo no borra el existente
        Assert.Equal(new DateTime(2026, 11, 5, 8, 0, 0), guardado.FechaCreacionUtc);
        Assert.Equal(new DateTime(2026, 11, 5, 10, 0, 0), guardado.UltimoAccesoUtc);
    }

    [Fact]
    public async Task Accesos_simultaneos_del_mismo_usuario_nuevo_no_duplican_ni_fallan()
    {
        var id = $"TEST\\{Guid.NewGuid():N}";
        var tareas = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var sincronizador = CrearSincronizador(out var sp, new FakeTimeProvider(DateTimeOffset.UtcNow));
            using var __ = sp;
            return await sincronizador.SincronizarAsync(id, "Concurrente", null);
        })).ToArray();

        var usuarios = await Task.WhenAll(tareas);

        Assert.Single(usuarios.Select(u => u.Id).Distinct());
        await using var db = bd.Crear();
        Assert.Equal(1, await db.Usuarios.CountAsync(u => u.Identificador == id));
    }
}
