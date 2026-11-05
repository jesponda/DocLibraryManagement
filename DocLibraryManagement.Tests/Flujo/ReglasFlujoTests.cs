using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Flujo;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Tests.Flujo;

public class ReglasFlujoTests
{
    private static PasoPlantillaDto P(TipoPaso clase, string nombre = "x") => new(99, clase, nombre);

    [Fact]
    public void El_orden_lo_da_la_posicion_y_se_ignora_el_que_venga()
    {
        var r = ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Revision, "A"), P(TipoPaso.Revision, "B"), P(TipoPaso.Aprobacion, "C")]);

        Assert.Equal([1, 2, 3], r.Select(p => p.Orden));
        Assert.Equal(["A", "B", "C"], r.Select(p => p.Nombre));
    }

    [Fact]
    public void Un_flujo_de_una_sola_aprobacion_es_valido()
    {
        Assert.Single(ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Aprobacion)]));
    }

    [Fact]
    public void El_flujo_no_puede_estar_vacio()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasFlujo.NormalizarPlantilla([]));
    }

    [Fact]
    public void Un_flujo_solo_de_revisiones_se_rechaza_porque_nadie_aprueba()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Revision), P(TipoPaso.Revision)]));
    }

    [Fact]
    public void La_ultima_etapa_debe_ser_una_aprobacion()
    {
        // Hay una aprobación, pero la decisión final sería una revisión.
        var ex = Assert.Throws<ReglaNegocioException>(() =>
            ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Aprobacion), P(TipoPaso.Revision)]));
        Assert.Contains("última", ex.Message);
    }

    [Fact]
    public void Un_nombre_vacio_toma_el_de_su_clase()
    {
        var r = ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Revision, "  "), P(TipoPaso.Aprobacion, "")]);

        Assert.Equal(["Revisión", "Aprobación"], r.Select(p => p.Nombre));
    }

    [Fact]
    public void Hay_limite_de_etapas_y_de_largo_de_nombre()
    {
        var once = Enumerable.Range(0, ReglasFlujo.MaximoPasos + 1).Select(_ => P(TipoPaso.Aprobacion)).ToList();
        Assert.Throws<ReglaNegocioException>(() => ReglasFlujo.NormalizarPlantilla(once));
        Assert.Throws<ReglaNegocioException>(() =>
            ReglasFlujo.NormalizarPlantilla([P(TipoPaso.Aprobacion, new string('x', ReglasFlujo.MaximoNombre + 1))]));

        var diez = Enumerable.Range(0, ReglasFlujo.MaximoPasos).Select(_ => P(TipoPaso.Aprobacion)).ToList();
        Assert.Equal(ReglasFlujo.MaximoPasos, ReglasFlujo.NormalizarPlantilla(diez).Count);
    }

    [Fact]
    public void Una_clase_inexistente_se_rechaza()
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasFlujo.NormalizarPlantilla([new PasoPlantillaDto(1, (TipoPaso)99, "x")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void El_comentario_obligatorio_no_admite_vacio(string? comentario)
    {
        Assert.Throws<ReglaNegocioException>(() => ReglasFlujo.NormalizarComentario(comentario, true, "falta"));
        Assert.Equal("", ReglasFlujo.NormalizarComentario(comentario, false, "falta"));
    }

    [Fact]
    public void El_comentario_se_recorta_y_tiene_limite()
    {
        Assert.Equal("ok", ReglasFlujo.NormalizarComentario("  ok  ", true, "falta"));
        Assert.Throws<ReglaNegocioException>(() =>
            ReglasFlujo.NormalizarComentario(new string('x', ReglasFlujo.MaximoComentario + 1), false, "falta"));
    }

    [Theory]
    [InlineData(TipoPaso.Revision, RolSistema.Revisor, EstadoRevision.EnRevision)]
    [InlineData(TipoPaso.Aprobacion, RolSistema.Aprobador, EstadoRevision.EnAprobacion)]
    public void Cada_clase_de_etapa_exige_su_rol_y_fija_el_estado_de_la_revision(TipoPaso clase, RolSistema rol, EstadoRevision estado)
    {
        Assert.Equal(rol, clase.RolRequerido());
        Assert.Equal(estado, clase.EstadoDeRevision());
    }

    [Fact]
    public void Solo_las_revisiones_en_trabajo_cuentan_como_abiertas()
    {
        Assert.All(new[] { EstadoRevision.Borrador, EstadoRevision.EnRevision, EstadoRevision.EnAprobacion, EstadoRevision.Aprobado },
            e => Assert.True(e.EsAbierta()));
        Assert.All(new[] { EstadoRevision.Vigente, EstadoRevision.Obsoleto, EstadoRevision.Cancelado },
            e => Assert.False(e.EsAbierta()));
    }
}
