using DocLibraryManagement.Domain;
using Microsoft.EntityFrameworkCore;

namespace DocLibraryManagement.Tests.Datos;

public class CatalogoYCodigosTests(BaseDatosFixture bd) : IClassFixture<BaseDatosFixture>
{
    [Fact]
    public void El_codigo_sigue_el_estandar_SITIO_TIPO_AREA_UNIDAD_NNN()
    {
        Assert.Equal("QRT-F-QA-II-001", Documento.ConstruirCodigo("QRT", "F", "QA", "II", 1));
        Assert.Equal("QRT-PI-QA-DC-012", Documento.ConstruirCodigo("QRT", "PI", "QA", "DC", 12));
        Assert.Equal("QRT-LOTO-ENG-MNT-123", Documento.ConstruirCodigo("QRT", "LOTO", "ENG", "MNT", 123));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_sitio_configurado_el_codigo_empieza_en_el_tipo(string? sitio)
    {
        Assert.Equal("F-QA-II-001", Documento.ConstruirCodigo(sitio, "F", "QA", "II", 1));
    }

    [Fact]
    public void Un_consecutivo_de_cuatro_digitos_no_se_trunca()
    {
        Assert.Equal("QRT-F-QA-II-1000", Documento.ConstruirCodigo("QRT", "F", "QA", "II", 1000));
    }

    [Fact]
    public void El_codigo_mas_largo_posible_cabe_en_la_columna()
    {
        var diezLetras = new string('X', 10);

        var codigo = Documento.ConstruirCodigo(diezLetras, diezLetras, diezLetras, diezLetras, 999_999);

        Assert.True(codigo.Length <= 60, $"El código más largo mide {codigo.Length}");
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
    public async Task El_consecutivo_no_se_repite_por_tipo_area_y_unidad()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);

        db.Documentos.Add(new Documento
        {
            Codigo = "OTRO-CODIGO",
            Titulo = "Mismo consecutivo",
            AreaId = doc.AreaId,
            SubAreaId = doc.SubAreaId,
            TipoDocumentoId = doc.TipoDocumentoId,
            Consecutivo = doc.Consecutivo,
            ResponsableId = doc.ResponsableId,
            FechaCreacionUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task El_mismo_consecutivo_si_se_permite_en_otra_unidad_del_area()
    {
        await using var db = bd.Crear();
        var doc = await BaseDatosFixture.CrearDocumentoAsync(db);
        var otraUnidad = new SubArea { AreaId = doc.AreaId, Codigo = "OTR", Nombre = "Otra unidad" };
        db.SubAreas.Add(otraUnidad);
        await db.SaveChangesAsync();

        db.Documentos.Add(new Documento
        {
            Codigo = "QRT-OTRO-001",
            Titulo = "Primero de su unidad",
            AreaId = doc.AreaId,
            SubAreaId = otraUnidad.Id,
            TipoDocumentoId = doc.TipoDocumentoId,
            Consecutivo = doc.Consecutivo,
            ResponsableId = doc.ResponsableId,
            FechaCreacionUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(); // no lanza: cada unidad lleva su propia numeración
    }

    [Fact]
    public async Task La_abreviatura_de_la_subarea_no_se_repite_dentro_del_area_pero_si_entre_areas()
    {
        await using var db = bd.Crear();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var area1 = new Area { Codigo = sufijo[..6] + "A1", Nombre = "Área 1" };
        var area2 = new Area { Codigo = sufijo[..6] + "A2", Nombre = "Área 2" };
        db.SubAreas.Add(new SubArea { Area = area1, Codigo = "SIP", Nombre = "Shipping" });
        db.SubAreas.Add(new SubArea { Area = area2, Codigo = "SIP", Nombre = "Shipments" }); // misma abreviatura en otra área: válido
        await db.SaveChangesAsync();

        db.SubAreas.Add(new SubArea { AreaId = area1.Id, Codigo = "SIP", Nombre = "Otro nombre" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Las_subareas_anteriores_al_estandar_pueden_no_tener_abreviatura_y_repetirse_sin_ella()
    {
        await using var db = bd.Crear();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var area = new Area { Codigo = sufijo, Nombre = "Área" };
        db.SubAreas.Add(new SubArea { Area = area, Nombre = "Anterior 1" });
        db.SubAreas.Add(new SubArea { Area = area, Nombre = "Anterior 2" });

        await db.SaveChangesAsync(); // dos nulos en la misma área: no cuentan para la unicidad
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
