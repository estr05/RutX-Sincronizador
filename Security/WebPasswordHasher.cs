using System.Security.Cryptography;

namespace Rutx.Sincronizador.Security;

/// <summary>
/// Hash de contraseñas web con PBKDF2-HMAC-SHA256 (OWASP 2023: ≥210.000
/// iteraciones). Formato versionado:
///
///     pbkdf2-sha256$210000$<salt base64>$<hash base64>
///
/// Reglas (Sprint 4 · Bloque 0):
///  - El hash se genera SIEMPRE con sal aleatoria (16 bytes) e iteraciones
///    configurables por construcción (parámetro con valor mínimo 210.000).
///  - La verificación es de tiempo constante (FixedTimeEquals).
///  - `NecesitaRehash` permite detectar contraseñas con iteraciones antiguas
///    para forzar rotación/actualización en el login (política de rotación).
///  - Nunca se almacena la contraseña en claro; nunca se versiona credencial.
/// </summary>
public static class WebPasswordHasher
{
    public const int IteracionesRecomendadas = 210_000;
    private const int TamanoSal = 16;
    private const int TamanoHash = 32;
    private const string Prefijo = "pbkdf2-sha256";

    public static string Hash(string password, int iteraciones = IteracionesRecomendadas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        if (iteraciones < IteracionesRecomendadas)
            throw new ArgumentOutOfRangeException(nameof(iteraciones), $"Mínimo {IteracionesRecomendadas} iteraciones.");

        var sal = RandomNumberGenerator.GetBytes(TamanoSal);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, sal, iteraciones, HashAlgorithmName.SHA256, TamanoHash);
        return $"{Prefijo}${iteraciones}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
            return false;

        var partes = storedHash.Split('$');
        if (partes.Length != 4 || partes[0] != Prefijo)
            return false;

        if (!int.TryParse(partes[1], out var iteraciones) || iteraciones < 1)
            return false;

        byte[] sal, esperado;
        try
        {
            sal = Convert.FromBase64String(partes[2]);
            esperado = Convert.FromBase64String(partes[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var calculado = Rfc2898DeriveBytes.Pbkdf2(password, sal, iteraciones, HashAlgorithmName.SHA256, esperado.Length);
        return CryptographicOperations.FixedTimeEquals(calculado, esperado);
    }

    /// <summary>True si el hash usa menos iteraciones que el estándar vigente (política de rotación).</summary>
    public static bool NecesitaRehash(string storedHash)
    {
        var partes = storedHash.Split('$');
        return partes.Length != 4 || !int.TryParse(partes[1], out var iteraciones) || iteraciones < IteracionesRecomendadas;
    }
}