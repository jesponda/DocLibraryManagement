using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Datos;

/// <summary>Reglas 7.1.4 y 7.1.5 garantizadas por la base de datos, no solo por la aplicación.</summary>
public class RestriccionesDeRevisionTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    private static readonly EstadoRevision[] Abiertas =
        [EstadoRevision.Borrador, EstadoRevision.EnRevision, EstadoRevision.EnAprobacion, EstadoRevision.Aprobado];

    [Fact]
    public async Task No_se_permiten_dos_revisiones_vigentes_del_mismo_documento()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 0, EstadoRevision.Vigente));
        await db.SaveChangesAsync();

        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 1, EstadoRevision.Vigente));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_Revision_UnicaVigente", ex.InnerException?.Message);
    }

    [Theory]
    [InlineData(EstadoRevision.Borrador, EstadoRevision.EnRevision)]
    [InlineData(EstadoRevision.Borrador, EstadoRevision.Aprobado)]
    [InlineData(EstadoRevision.EnAprobacion, EstadoRevision.Borrador)]
    public async Task No_se_permiten_dos_revisiones_abiertas_del_mismo_documento(EstadoRevision primera, EstadoRevision segunda)
    {
        Assert.Contains(primera, Abiertas);
        Assert.Contains(segunda, Abiertas);

        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 0, primera));
        await db.SaveChangesAsync();

        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 1, segunda));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("UX_Revision_UnicaAbierta", ex.InnerException?.Message);
    }

    [Fact]
    public async Task Una_vigente_puede_convivir_con_una_borrador_y_con_obsoletas()
    {
        // Es el escenario normal de una nueva revisión en curso: la vigente sigue en uso.
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        db.Revisiones.AddRange(
            BaseDatosFixture.NuevaRevision(doc, 0, EstadoRevision.Obsoleto),
            BaseDatosFixture.NuevaRevision(doc, 1, EstadoRevision.Obsoleto),
            BaseDatosFixture.NuevaRevision(doc, 2, EstadoRevision.Vigente),
            BaseDatosFixture.NuevaRevision(doc, 3, EstadoRevision.Borrador));

        await db.SaveChangesAsync();

        Assert.Equal(4, await db.Revisiones.CountAsync(r => r.DocumentoId == doc.Id));
    }

    [Fact]
    public async Task Cada_documento_tiene_su_propia_vigente()
    {
        await using var db = bd.Crear();
        var doc1 = await BaseDatosFixture.CrearDocumentoAsync(db);
        var doc2 = await BaseDatosFixture.CrearDocumentoAsync(db);

        db.Revisiones.AddRange(
            BaseDatosFixture.NuevaRevision(doc1, 0, EstadoRevision.Vigente),
            BaseDatosFixture.NuevaRevision(doc2, 0, EstadoRevision.Vigente));

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task No_se_repite_el_numero_de_revision_en_un_documento()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 1, EstadoRevision.Obsoleto));
        await db.SaveChangesAsync();

        db.Revisiones.Add(BaseDatosFixture.NuevaRevision(doc, 1, EstadoRevision.Obsoleto));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Una_revision_menor_tiene_numeracion_propia()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        db.Revisiones.AddRange(
            BaseDatosFixture.NuevaRevision(doc, 3, EstadoRevision.Obsoleto),
            BaseDatosFixture.NuevaRevision(doc, 3, EstadoRevision.Vigente, menor: 1));

        await db.SaveChangesAsync();
    }
}
