using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Documentos;

namespace DocLibraryManagement.Tests.Documentos;

public class ReglasRevisionTests
{
    private static readonly DateTime Ahora = new(2026, 11, 5, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0, TipoRevisionNueva.Mayor, 1, 0)]
    [InlineData(3, 0, TipoRevisionNueva.Mayor, 4, 0)]
    [InlineData(3, 2, TipoRevisionNueva.Mayor, 4, 0)]   // una mayor reinicia la menor
    [InlineData(3, 0, TipoRevisionNueva.Menor, 3, 1)]
    [InlineData(3, 1, TipoRevisionNueva.Menor, 3, 2)]
    [InlineData(0, 0, TipoRevisionNueva.Menor, 0, 1)]
    public void La_numeracion_sigue_a_la_revision_vigente(int mayor, int menor, TipoRevisionNueva tipo, int esperadaMayor, int esperadaMenor)
    {
        Assert.Equal((esperadaMayor, esperadaMenor), ReglasRevision.SiguienteNumero(mayor, menor, tipo));
    }

    [Fact]
    public void Sin_fecha_la_vigencia_es_inmediata()
    {
        var (vigencia, inmediata) = ReglasRevision.ResolverVigencia(Ahora, null);

        Assert.True(inmediata);
        Assert.Equal(Ahora, vigencia);
    }

    [Theory]
    [InlineData(-30)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Una_fecha_pasada_o_actual_se_trata_como_inmediata(int dias)
    {
        var (vigencia, inmediata) = ReglasRevision.ResolverVigencia(Ahora, Ahora.AddDays(dias));

        Assert.True(inmediata);
        Assert.Equal(Ahora, vigencia); // se usa el instante real, no la fecha pasada que se pidió
    }

    [Fact]
    public void Una_fecha_futura_se_respeta()
    {
        var futura = Ahora.AddDays(10);

        var (vigencia, inmediata) = ReglasRevision.ResolverVigencia(Ahora, futura);

        Assert.False(inmediata);
        Assert.Equal(futura, vigencia);
    }

    [Fact]
    public void El_limite_es_un_ano_hacia_adelante()
    {
        var (_, inmediata) = ReglasRevision.ResolverVigencia(Ahora, Ahora.AddDays(ReglasRevision.MaximoDiasVigenciaDiferida));
        Assert.False(inmediata);

        Assert.Throws<ReglaNegocioException>(() =>
            ReglasRevision.ResolverVigencia(Ahora, Ahora.AddDays(ReglasRevision.MaximoDiasVigenciaDiferida).AddMinutes(1)));
    }

    [Theory]
    [InlineData(12, 2027, 11, 5)]
    [InlineData(6, 2027, 5, 5)]
    [InlineData(1, 2026, 12, 5)]
    public void La_proxima_revision_suma_los_meses_del_tipo(int meses, int anio, int mes, int dia)
    {
        Assert.Equal(new DateTime(anio, mes, dia, 8, 0, 0, DateTimeKind.Utc), ReglasRevision.ProximaRevision(Ahora, meses));
    }

    [Fact]
    public void Sumar_un_mes_a_fin_de_mes_no_se_desborda()
    {
        var treintaYUno = new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2027, 2, 28, 0, 0, 0, DateTimeKind.Utc), ReglasRevision.ProximaRevision(treintaYUno, 1));
    }
}
