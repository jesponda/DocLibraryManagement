using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Datos;

public class CatalogoYCodigosTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Fact]
    public void El_codigo_sigue_el_formato_TTT_AAA_NNN()
    {
        Assert.Equal("PRO-CAL-012", Documento.ConstruirCodigo("PRO", "CAL", 12));
        Assert.Equal("INS-MAN-001", Documento.ConstruirCodigo("INS", "MAN", 1));
    }

    [Theory]
    [InlineData(0, 0, "Rev.00")]
    [InlineData(3, 0, "Rev.03")]
    [InlineData(3, 1, "Rev.03.1")]
    public void La_etiqueta_de_revision_distingue_mayor_y_menor(int mayor, int menor, string esperado)
    {
        var revision = new RevisionDocumento { Mayor = mayor, Menor = menor, MotivoCambio = "x" };

        Assert.Equal(esperado, revision.Etiqueta);
    }

    [Fact]
    public async Task El_codigo_de_documento_es_unico()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);

        db.Documentos.Add(new Documento
        {
            Codigo = doc.Codigo,
            Titulo = "Duplicado",
            AreaId = doc.AreaId,
            TipoDocumentoId = doc.TipoDocumentoId,
            Consecutivo = 2,
            ResponsableId = doc.ResponsableId,
            FechaCreacionUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task El_consecutivo_no_se_repite_por_tipo_y_area()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);

        db.Documentos.Add(new Documento
        {
            Codigo = "OTRO-CODIGO",
            Titulo = "Mismo consecutivo",
            AreaId = doc.AreaId,
            TipoDocumentoId = doc.TipoDocumentoId,
            Consecutivo = doc.Consecutivo,
            ResponsableId = doc.ResponsableId,
            FechaCreacionUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task La_abreviatura_de_area_y_de_tipo_es_unica()
    {
        await using var db = bd.Crear();
        var area = new Area { Codigo = "DUP", Nombre = "Primera" };
        db.Areas.Add(area);
        await db.SaveChangesAsync();

        db.Areas.Add(new Area { Codigo = "DUP", Nombre = "Segunda" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task No_se_puede_eliminar_un_area_con_documentos()
    {
        int areaId;
        await using (var creacion = bd.Crear())
        {
            areaId = (await BaseDatosFixture.CrearDocumentoAsync(creacion)).AreaId;
        }

        // Contexto nuevo: sin documentos cargados, para que la negativa venga de la BD y no de EF.
        await using var db = bd.Crear();
        db.Areas.Remove(await db.Areas.FirstAsync(a => a.Id == areaId));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Una_subarea_pertenece_a_un_area_y_hereda_sus_tipos_si_no_tiene_propios()
    {
        await using var db = bd.Crear();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var tipo = new TipoDocumento { Codigo = sufijo, Nombre = "Tipo" };
        var area = new Area { Codigo = sufijo, Nombre = "Área" };
        var sub = new SubArea { Area = area, Nombre = "Subárea" };
        area.TiposDocumento.Add(new AreaTipoDocumento { TipoDocumento = tipo });
        db.SubAreas.Add(sub);
        await db.SaveChangesAsync();

        var cargada = await db.SubAreas
            .Include(s => s.TiposDocumento)
            .Include(s => s.Area!).ThenInclude(a => a.TiposDocumento)
            .FirstAsync(s => s.Id == sub.Id);

        Assert.Empty(cargada.TiposDocumento);
        Assert.Single(cargada.Area!.TiposDocumento);
    }

    [Fact]
    public async Task La_subarea_no_se_repite_dentro_del_area()
    {
        await using var db = bd.Crear();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var area = new Area { Codigo = sufijo, Nombre = "Área" };
        db.SubAreas.Add(new SubArea { Area = area, Nombre = "Línea 1" });
        await db.SaveChangesAsync();

        db.SubAreas.Add(new SubArea { AreaId = area.Id, Nombre = "Línea 1" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Los_meses_de_revision_deben_ser_positivos()
    {
        await using var db = bd.Crear();
        db.TiposDocumento.Add(new TipoDocumento { Codigo = "NEG", Nombre = "Inválido", MesesRevision = 0 });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
