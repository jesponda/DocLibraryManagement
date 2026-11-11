using DocLibraryManagement.Application.Comun;
using DocLibraryManagement.Application.Reportes;
using DocLibraryManagement.Application.Vencimientos;
using DocLibraryManagement.Domain;

namespace DocLibraryManagement.Tests.Reportes;

public class ReglasReporteTests
{
    private static readonly DateTime Ahora = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

    // ---------- Días para vencer (con signo) ----------

    [Theory]
    [InlineData(20, 20)]
    [InlineData(0.2, 1)]      // faltan horas: «1 día», no «0»
    [InlineData(7, 7)]
    [InlineData(0, 0)]        // vence ahora mismo: «0» (vencida hoy)
    [InlineData(-0.5, 0)]     // venció hace medio día: todavía «hoy»
    [InlineData(-1, -1)]
    [InlineData(-3.9, -3)]    // días completos de atraso
    [InlineData(-400, -400)]
    public void Los_dias_para_vencer_llevan_signo_y_cuentan_dias_completos(double dias, int esperado)
    {
        Assert.Equal(esperado, ReglasReporte.DiasParaVencer(Ahora.AddDays(dias), Ahora));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.9, 0)]
    [InlineData(1, 1)]
    [InlineData(10.5, 10)]
    [InlineData(-5, 0)]       // una fecha futura no da «días negativos esperando»
    public void Los_dias_esperando_son_completos_y_nunca_negativos(double dias, int esperado)
    {
        Assert.Equal(esperado, ReglasReporte.DiasEsperando(Ahora.AddDays(-dias), Ahora));
    }

    // ---------- Rango de fechas en hora local ----------

    [Fact]
    public void El_rango_cubre_desde_el_inicio_del_primer_dia_hasta_el_inicio_del_dia_siguiente_al_ultimo()
    {
        var utc = TimeZoneInfo.Utc;

        var (desde, hasta) = ReglasReporte.RangoUtc(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 7), utc);

        Assert.Equal(new DateTime(2026, 11, 5, 0, 0, 0), desde);
        Assert.Equal(new DateTime(2026, 11, 8, 0, 0, 0), hasta);   // el 7 completo queda dentro; el 8 a las 00:00 no
    }

    [Fact]
    public void El_rango_respeta_la_zona_horaria_de_la_planta()
    {
        // UTC-6 (sin horario de verano): el día local empieza a las 06:00 UTC.
        var zona = TimeZoneInfo.CreateCustomTimeZone("Planta", TimeSpan.FromHours(-6), "Planta", "Planta");

        var (desde, hasta) = ReglasReporte.RangoUtc(new DateOnly(2026, 11, 5), new DateOnly(2026, 11, 5), zona);

        Assert.Equal(new DateTime(2026, 11, 5, 6, 0, 0), desde);
        Assert.Equal(new DateTime(2026, 11, 6, 6, 0, 0), hasta);
    }

    [Fact]
    public void Un_extremo_vacio_deja_el_rango_abierto_por_ese_lado()
    {
        var (soloDesde, sinHasta) = ReglasReporte.RangoUtc(new DateOnly(2026, 11, 5), null, TimeZoneInfo.Utc);
        var (sinDesde, soloHasta) = ReglasReporte.RangoUtc(null, new DateOnly(2026, 11, 5), TimeZoneInfo.Utc);
        var (nada1, nada2) = ReglasReporte.RangoUtc(null, null, TimeZoneInfo.Utc);

        Assert.NotNull(soloDesde);
        Assert.Null(sinHasta);
        Assert.Null(sinDesde);
        Assert.NotNull(soloHasta);
        Assert.Null(nada1);
        Assert.Null(nada2);
    }

    [Fact]
    public void Un_rango_al_reves_se_rechaza()
    {
        Assert.Throws<ReglaNegocioException>(() =>
            ReglasReporte.RangoUtc(new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 7), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Un_dia_cuya_medianoche_no_existe_por_cambio_de_horario_no_revienta()
    {
        // Zona con adelanto a las 00:00 → 01:00 (como algunas de Sudamérica): la medianoche local no existe ese día.
        var regla = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 11, 8),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 12, 20));
        var zona = TimeZoneInfo.CreateCustomTimeZone("Sur", TimeSpan.FromHours(-3), "Sur", "Sur", "Sur", [regla]);

        var (desde, _) = ReglasReporte.RangoUtc(new DateOnly(2026, 11, 8), null, zona);

        Assert.NotNull(desde);
    }

    // ---------- Texto de filtro ----------

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("  hola  ", "hola")]
    public void El_texto_de_filtro_se_limpia(string? entrada, string? esperado)
    {
        Assert.Equal(esperado, ReglasReporte.LimpiarTexto(entrada, "Campo"));
    }

    [Fact]
    public void Un_texto_de_filtro_desmesurado_se_rechaza_y_el_mensaje_dice_cual_campo()
    {
        var ex = Assert.Throws<ReglaNegocioException>(() =>
            ReglasReporte.LimpiarTexto(new string('x', ReglasReporte.MaximoTextoFiltro + 1), "Usuario"));

        Assert.Contains("Usuario", ex.Message);
        Assert.Equal(new string('x', ReglasReporte.MaximoTextoFiltro), ReglasReporte.LimpiarTexto(new string('x', ReglasReporte.MaximoTextoFiltro), "Usuario"));
    }

    // ---------- Textos ----------

    [Fact]
    public void Todos_los_valores_de_cada_enumeracion_tienen_nombre_en_espanol()
    {
        foreach (var e in Enum.GetValues<EstadoVencimiento>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreVencimiento(e)));
        }

        foreach (var e in Enum.GetValues<TipoTareaReporte>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreTipoTarea(e)));
        }

        foreach (var e in Enum.GetValues<EstadoPaso>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreEstadoPaso(e)));
        }

        foreach (var e in Enum.GetValues<EstadoFlujo>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreEstadoFlujo(e)));
        }

        foreach (var e in Enum.GetValues<EstadoRevision>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreEstadoRevision(e)));
        }

        foreach (var e in Enum.GetValues<EstadoSolicitudSinCambios>())
        {
            Assert.False(string.IsNullOrWhiteSpace(ReglasReporte.NombreSolicitud(e)));
        }
    }

    // ---------- Tablas exportables ----------

    [Fact]
    public void Cada_fila_de_cada_tabla_tiene_tantas_celdas_como_encabezados()
    {
        var historial = new HistorialDocumentoDto(1, "PRO-CAL-001", "T", "CAL – Calidad", "Procedimiento", EstadoDocumento.Activo, "Ana", Ahora,
        [
            // Sin envíos ni solicitudes.
            new HistorialRevisionDto(1, "Rev.00", EstadoRevision.Borrador, "m", null, "Ana", Ahora, null, null, null, [], []),
            // Con envío sin pasos, con pasos y con una solicitud.
            new HistorialRevisionDto(2, "Rev.01", EstadoRevision.Vigente, "m", "d", "Ana", Ahora, Ahora, Ahora, Ahora.AddYears(1),
            [
                new HistorialEnvioDto(1, EstadoFlujo.Cancelado, Ahora, Ahora, "Ana", "motivo", []),
                new HistorialEnvioDto(2, EstadoFlujo.Aprobado, Ahora, Ahora, "Ana", null,
                [
                    new HistorialPasoDto(1, TipoPaso.Revision, "Revisión técnica", "Beto", EstadoPaso.Aprobado, "Beto", Ahora, "ok"),
                    new HistorialPasoDto(2, TipoPaso.Aprobacion, "Aprobación", "Cris", EstadoPaso.Aprobado, "Cris", Ahora, null),
                ]),
            ],
            [new SolicitudSinCambiosDto(1, 2, 1, "PRO-CAL-001", "T", "Rev.01", "Ana", "sin cambios", EstadoSolicitudSinCambios.Aprobada, Ahora, Ahora, Ahora.AddYears(1), "Cris", "ok", Ahora)]),
        ]);

        foreach (var tabla in new[]
                 {
                     TablasReporte.Historial(historial),
                     TablasReporte.Vencimientos([new FilaVencimientoDto(1, 1, "C", "T", "A", "Tipo", "Rev.00", Ahora, EstadoVencimiento.Vencido, -2, "R", null, true)]),
                     TablasReporte.Tareas([new FilaTareaDto(Guid.NewGuid(), "U", TipoTareaReporte.Revision, 1, "C", "T", "A", "Rev.00", "E", Ahora, 3)]),
                     TablasReporte.Auditoria([new EventoAuditoriaDto(1, Ahora, "u", "a", "e", null, null, null, null, null)]),
                 })
        {
            Assert.NotEmpty(tabla.Filas);
            Assert.All(tabla.Filas, f => Assert.Equal(tabla.Encabezados.Count, f.Count));
        }
    }

    [Fact]
    public void El_historial_exportado_no_pierde_ningun_dato_de_las_etapas_ni_de_las_solicitudes()
    {
        var h = new HistorialDocumentoDto(1, "PRO-CAL-001", "T", "CAL – Calidad", "Procedimiento", EstadoDocumento.Activo, "Ana", Ahora,
        [
            new HistorialRevisionDto(1, "Rev.00", EstadoRevision.Vigente, "Emisión", null, "Ana", Ahora, Ahora, Ahora, Ahora.AddYears(1),
            [
                new HistorialEnvioDto(1, EstadoFlujo.Rechazado, Ahora, Ahora, "Ana", null,
                [
                    new HistorialPasoDto(1, TipoPaso.Revision, "Revisión técnica", "Beto", EstadoPaso.Rechazado, "Beto", Ahora, "Falta el anexo"),
                    new HistorialPasoDto(2, TipoPaso.Aprobacion, "Aprobación", "Cris", EstadoPaso.Omitido, null, null, null),
                ]),
            ],
            [new SolicitudSinCambiosDto(1, 1, 1, "PRO-CAL-001", "T", "Rev.00", "Ana", "Sigue igual", EstadoSolicitudSinCambios.Rechazada, Ahora, Ahora, null, "Cris", "Falta evidencia", Ahora)]),
        ]);

        var tabla = TablasReporte.Historial(h);

        Assert.Equal(3, tabla.Filas.Count); // dos etapas + una solicitud
        var texto = string.Join('|', tabla.Filas.SelectMany(f => f).Select(c => c?.ToString()));
        Assert.Contains("Falta el anexo", texto);
        Assert.Contains("Revisión técnica", texto);
        Assert.Contains("Omitida", texto);
        Assert.Contains("Sigue igual | Decisión: Falta evidencia", texto);
        Assert.Contains("Rechazada", texto);
    }
}
