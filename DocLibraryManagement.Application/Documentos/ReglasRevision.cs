using DocLibraryManagement.Application.Comun;

namespace DocLibraryManagement.Application.Documentos;

public enum TipoRevisionNueva
{
    /// <summary>Cambio de fondo: Rev.00 → Rev.01. Usa el flujo completo del tipo de documento.</summary>
    Mayor = 1,

    /// <summary>Corrección editorial: Rev.03 → Rev.03.1. Un solo aprobador (el dueño del área).</summary>
    Menor = 2,
}

/// <summary>Reglas puras de numeración y vigencia (7.1 y 7.3); se prueban sin base de datos.</summary>
public static class ReglasRevision
{
    /// <summary>Hasta cuánto en el futuro puede programarse la entrada en vigor.</summary>
    public const int MaximoDiasVigenciaDiferida = 365;

    /// <summary>
    /// Número de la revisión que sigue a la vigente. Una mayor suma 1 y reinicia la menor;
    /// una menor suma 1 a la menor: Rev.03 → Rev.03.1 → Rev.03.2 → (mayor) Rev.04.
    /// </summary>
    public static (int Mayor, int Menor) SiguienteNumero(int vigenteMayor, int vigenteMenor, TipoRevisionNueva tipo) =>
        tipo == TipoRevisionNueva.Mayor
            ? (vigenteMayor + 1, 0)
            : (vigenteMayor, vigenteMenor + 1);

    /// <summary>
    /// Fecha en que la revisión aprobada entra en vigor. Sin fecha, o con una que ya pasó, es de inmediato.
    /// Una fecha futura se respeta hasta el máximo permitido.
    /// </summary>
    public static (DateTime VigenciaUtc, bool Inmediata) ResolverVigencia(DateTime ahoraUtc, DateTime? solicitadaUtc)
    {
        if (solicitadaUtc is not { } pedida || pedida <= ahoraUtc)
        {
            return (ahoraUtc, true);
        }

        if (pedida > ahoraUtc.AddDays(MaximoDiasVigenciaDiferida))
        {
            throw new ReglaNegocioException(
                $"La fecha de entrada en vigor no puede ser mayor a {MaximoDiasVigenciaDiferida} días a partir de hoy.");
        }

        return (pedida, false);
    }

    /// <summary>Próxima revisión periódica: la vigencia más los meses del tipo (7.4).</summary>
    public static DateTime ProximaRevision(DateTime vigenciaUtc, int mesesRevision) => vigenciaUtc.AddMonths(mesesRevision);
}
