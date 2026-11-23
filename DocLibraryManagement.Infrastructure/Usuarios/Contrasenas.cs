using System.Security.Cryptography;

namespace DocLibraryManagement.Infrastructure.Usuarios;

/// <summary>
/// Hash y verificación de contraseñas con PBKDF2-SHA256 (sal aleatoria por contraseña, comparación en tiempo constante).
/// Formato guardado: <c>PBKDF2$iteraciones$sal$hash</c> (Base64), para poder subir las iteraciones sin invalidar lo existente.
/// </summary>
internal static class Contrasenas
{
    private const string Prefijo = "PBKDF2";
    private const int Iteraciones = 310_000;
    private const int BytesSal = 16;
    private const int BytesHash = 32;

    // Sin letras ambiguas (0/O, 1/l/I) para que una contraseña temporal pueda dictarse o copiarse sin errores.
    private const string Alfabeto = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    // Para gastar el mismo tiempo cuando el usuario no existe (no revelar quién tiene cuenta por la demora).
    private static readonly string HashDeRelleno = Hashear("relleno-para-igualar-tiempos");

    public static string Hashear(string contrasena)
    {
        var sal = RandomNumberGenerator.GetBytes(BytesSal);
        var hash = Rfc2898DeriveBytes.Pbkdf2(contrasena, sal, Iteraciones, HashAlgorithmName.SHA256, BytesHash);
        return $"{Prefijo}${Iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verificar(string contrasena, string? guardado)
    {
        if (!TryLeer(guardado, out var iteraciones, out var sal, out var esperado))
        {
            // Mismo costo que una verificación real.
            _ = TryLeer(HashDeRelleno, out var it, out var s, out var h)
                && CryptographicOperations.FixedTimeEquals(Rfc2898DeriveBytes.Pbkdf2(contrasena, s, it, HashAlgorithmName.SHA256, h.Length), h);
            return false;
        }

        var calculado = Rfc2898DeriveBytes.Pbkdf2(contrasena, sal, iteraciones, HashAlgorithmName.SHA256, esperado.Length);
        return CryptographicOperations.FixedTimeEquals(calculado, esperado);
    }

    /// <summary>Contraseña temporal legible: tres grupos de cuatro (p. ej. <c>Kq7m-Xt9c-Pw3z</c>) con mayúscula, minúscula y número.</summary>
    public static string GenerarTemporal()
    {
        while (true)
        {
            var letras = new char[12];
            for (var i = 0; i < letras.Length; i++)
            {
                letras[i] = Alfabeto[RandomNumberGenerator.GetInt32(Alfabeto.Length)];
            }

            if (letras.Any(char.IsUpper) && letras.Any(char.IsLower) && letras.Any(char.IsDigit))
            {
                var t = new string(letras);
                return $"{t[..4]}-{t[4..8]}-{t[8..]}";
            }
        }
    }

    private static bool TryLeer(string? guardado, out int iteraciones, out byte[] sal, out byte[] hash)
    {
        iteraciones = 0;
        sal = [];
        hash = [];
        var partes = guardado?.Split('$');
        if (partes is not { Length: 4 } || partes[0] != Prefijo || !int.TryParse(partes[1], out iteraciones) || iteraciones < 1)
        {
            return false;
        }

        try
        {
            sal = Convert.FromBase64String(partes[2]);
            hash = Convert.FromBase64String(partes[3]);
            return sal.Length > 0 && hash.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
