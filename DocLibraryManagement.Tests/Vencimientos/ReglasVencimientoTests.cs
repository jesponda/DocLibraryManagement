using DocLibraryManagement.Application.Vencimientos;

namespace DocLibraryManagement.Tests.Vencimientos;

public class ReglasVencimientoTests
{
    private static readonly DateTime Ahora = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

    // ---------- Clasificación ----------

    [Fact]
    public void Sin_fecha_de_proxima_revision_no_hay_nada_que_avisar()
    {
        Assert.Equal(EstadoVencimiento.AlDia, ReglasVencimiento.Clasificar(null, Ahora));
    }

    [Theory]
    [InlineData(400, EstadoVencimiento.AlDia)]
    [InlineData(31, EstadoVencimiento.AlDia)]
    [InlineData(30.5, EstadoVencimiento.AlDia)]
    [InlineData(30, EstadoVencimiento.PorVencer30)]   // exactamente 30 días: ya se avisa
    [InlineData(29.9, EstadoVencimiento.PorVencer30)]
    [InlineData(8, EstadoVencimiento.PorVencer30)]
    [InlineData(7.01, EstadoVencimiento.PorVencer30)]
    [InlineData(7, EstadoVencimiento.PorVencer7)]     // exactamente 7 días: aviso urgente
    [InlineData(1, EstadoVencimiento.PorVencer7)]
    [InlineData(0.01, EstadoVencimiento.PorVencer7)]
    public void Los_limites_de_30_y_7_dias_son_inclusivos(double diasRestantes, EstadoVencimiento esperado)
    {
        Assert.Equal(esperado, ReglasVencimiento.Clasificar(Ahora.AddDays(diasRestantes), Ahora));
    }

    [Theory]
    [InlineData(0)]       // el instante exacto del vencimiento ya es «vencido»
    [InlineData(-0.01)]
    [InlineData(-1)]
    [InlineData(-400)]
    public void Desde_la_fecha_en_adelante_esta_vencido(double dias)
    {
        Assert.Equal(EstadoVencimiento.Vencido, ReglasVencimiento.Clasificar(Ahora.AddDays(dias), Ahora));
    }

    // ---------- Días restantes ----------

    [Theory]
    [InlineData(30, 30)]
    [InlineData(29.2, 30)]   // se redondea hacia arriba: faltan «hasta» 30
    [InlineData(0.25, 1)]    // faltan 6 horas → «1 día», nunca «0 días» sin estar vencido
    [InlineData(7, 7)]
    public void Los_dias_restantes_se_redondean_hacia_arriba(double dias, int esperado)
    {
        Assert.Equal(esperado, ReglasVencimiento.DiasRestantes(Ahora.AddDays(dias), Ahora));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Vencido_no_tiene_dias_restantes(double dias)
    {
        Assert.Equal(0, ReglasVencimiento.DiasRestantes(Ahora.AddDays(dias), Ahora));
    }

    // ---------- Cuándo se puede proponer «sin cambios» ----------

    [Theory]
    [InlineData(200, false)]  // con meses de anticipación no: empujaría el vencimiento sin haber revisado nada
    [InlineData(31, false)]
    [InlineData(30, true)]
    [InlineData(5, true)]
    [InlineData(-3, true)]    // vencido: precisamente el caso para el que sirve
    public void Solo_toca_proponer_dentro_de_la_ventana_de_aviso_o_vencido(double dias, bool puede)
    {
        Assert.Equal(puede, ReglasVencimiento.PuedeProponerSinCambios(Ahora.AddDays(dias), Ahora));
    }

    [Fact]
    public void Sin_fecha_no_se_puede_proponer()
    {
        Assert.False(ReglasVencimiento.PuedeProponerSinCambios(null, Ahora));
    }

    // ---------- Nueva fecha ----------

    [Fact]
    public void Si_todavia_no_vence_la_nueva_fecha_cuenta_un_periodo_completo_desde_la_fecha_vigente()
    {
        var actual = Ahora.AddDays(20);

        var nueva = ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(actual, Ahora, 12);

        Assert.Equal(actual.AddMonths(12), nueva); // no se pierden los 20 días que faltaban
    }

    [Fact]
    public void Si_ya_vencio_la_nueva_fecha_cuenta_desde_hoy_y_no_hereda_el_atraso()
    {
        var actual = Ahora.AddDays(-90);

        var nueva = ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(actual, Ahora, 12);

        Assert.Equal(Ahora.AddMonths(12), nueva);
    }

    [Fact]
    public void La_nueva_fecha_respeta_los_meses_configurados_del_tipo()
    {
        Assert.Equal(Ahora.AddMonths(6), ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(Ahora.AddDays(-1), Ahora, 6));
        Assert.Equal(Ahora.AddMonths(24), ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(Ahora.AddDays(-1), Ahora, 24));
    }

    [Fact]
    public void La_nueva_fecha_siempre_queda_fuera_de_la_ventana_de_aviso()
    {
        // Tras aprobar, el documento no debe volver a verse como por vencer.
        foreach (var dias in new double[] { 30, 7, 1, 0, -1, -400 })
        {
            var nueva = ReglasVencimiento.NuevaFechaTrasRevisionSinCambios(Ahora.AddDays(dias), Ahora, 12);

            Assert.Equal(EstadoVencimiento.AlDia, ReglasVencimiento.Clasificar(nueva, Ahora));
        }
    }

    // ---------- Marca de la fecha ----------

    [Fact]
    public void La_marca_distingue_cada_fecha_y_es_estable()
    {
        var a = ReglasVencimiento.MarcaFecha(new DateTime(2027, 11, 5, 8, 0, 0, DateTimeKind.Utc));
        var b = ReglasVencimiento.MarcaFecha(new DateTime(2028, 11, 5, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal("202711050800", a);
        Assert.NotEqual(a, b);
    }
}
