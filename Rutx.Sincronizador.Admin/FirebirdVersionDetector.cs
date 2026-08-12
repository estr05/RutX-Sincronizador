using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Informacion versionada de una instancia Firebird detectada.
/// </summary>
public sealed class FirebirdVersionInfo
{
    /// <summary>Numero de version completo (ej. "3.0.11", "5.0.0", "2.5.9").</summary>
    public string Version { get; set; } = "";

    /// <summary>ODS (On-Disk Structure) leido del header de la .fdb.</summary>
    public int Ods { get; set; }

    /// <summary>Nombre descriptivo (ej. "Firebird 3.0", "Firebird 5.0").</summary>
    public string Nombre { get; set; } = "";

    /// <summary>True si es una version soportada para Microsip (>= 2.5).</summary>
    public bool Soportada { get; set; }

    /// <summary>Advertencias o recomendaciones para esta version.</summary>
    public string? Nota { get; set; }

    /// <summary>Como se detecto (ODS, MON$, RDB$GET_CONTEXT, etc.).</summary>
    public string MetodoDeteccion { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrEmpty(Version)
            ? $"Firebird (ODS {Ods}) — version desconocida"
            : $"{Nombre} (ODS {Ods}) — {Version}";
}

/// <summary>
/// Detecta la version de Firebird usando multiples estrategias:
///   1. ODS del header .fdb (sin conexion, todas las versiones)
///   2. MON$DATABASE_VERSION (Firebird 2.5+)
///   3. RDB$GET_CONTEXT('SYSTEM','ENGINE_VERSION') (Firebird 3.0+)
///   4. Fallback: solo MON$DATABASE_NAME
///
/// 100% SOLO LECTURA — nunca escribe en la BD.
/// </summary>
public static class FirebirdVersionDetector
{
    // ==================================================================
    // MAPA ODS → FIREBIRD VERSION (todas las conocidas)
    // ==================================================================
    // ODS = On-Disk Structure. Cada version mayor de Firebird usa un ODS
    // distinto. El ODS se lee directamente del header de la .fdb (byte 16-17),
    // asi que funciona sin abrir conexion y con cualquier version.
    //
    // Fuente: Firebird Release Notes y fb_types.h
    // ==================================================================
    private static readonly Dictionary<int, (string Version, string Nombre)> OdsMap = new()
    {
        // Firebird 1.0 (InterBase 6.0)
        [10] = ("1.0",   "Firebird 1.0"),
        [11] = ("1.5",   "Firebird 1.5"),
        // Firebird 2.x
        [12] = ("2.0",   "Firebird 2.0"),
        [13] = ("2.1",   "Firebird 2.1"),
        [14] = ("2.5",   "Firebird 2.5"),
        // Firebird 3.x
        [15] = ("3.0",   "Firebird 3.0"),
        // Firebird 4.x
        [16] = ("4.0",   "Firebird 4.0"),
        // Firebird 5.x
        [17] = ("5.0",   "Firebird 5.0"),
    };

    // Version minima soportada para Microsip
    private const int OdsMinimoSoportado = 13; // Firebird 2.1

    // ==================================================================
    // METODO PUBLICO PRINCIPAL
    // ==================================================================

    /// <summary>
    /// Detecta la version de Firebird usando todas las estrategias disponibles.
    /// Puede recibir la cadena de conexion (si ya se conecto) o solo la ruta .fdb.
    /// </summary>
    public static FirebirdVersionInfo Detectar(string rutaFdb, string? cadenaConexion = null)
    {
        var info = new FirebirdVersionInfo();

        // ---- Estrategia 1: ODS desde el header del .fdb (sin conexion) ----
        try
        {
            var ods = LeerOdsDeArchivo(rutaFdb);
            if (ods > 0)
            {
                info.Ods = ods;
                info.MetodoDeteccion = "ODS (header .fdb)";
                if (OdsMap.TryGetValue(ods, out var mapping))
                {
                    info.Version = mapping.Version;
                    info.Nombre = mapping.Nombre;
                }
            }
        }
        catch { /* no se pudo leer el header */ }

        // ---- Estrategia 2 y 3: queries SQL (requiere conexion) ----
        if (!string.IsNullOrEmpty(cadenaConexion))
        {
            try
            {
                DetectarViaSql(cadenaConexion, info);
            }
            catch { /* la conexion fallo, usamos lo que tengamos del ODS */ }
        }

        // ---- Completar campos faltantes ----
        if (string.IsNullOrEmpty(info.Nombre) && info.Ods > 0)
        {
            info.Nombre = OdsMap.TryGetValue(info.Ods, out var m)
                ? m.Nombre
                : $"Firebird (ODS {info.Ods})";
        }

        if (string.IsNullOrEmpty(info.Version) && info.Ods > 0)
        {
            info.Version = OdsMap.TryGetValue(info.Ods, out var m2)
                ? m2.Version
                : "desconocida";
        }

        // Soportada?
        info.Soportada = info.Ods >= OdsMinimoSoportado || info.Ods == 0; // 0 = no detectado, asumir ok

        // Notas para versiones problematicas
        if (info.Ods is >= 10 and < 13)
            info.Nota = "Firebird 1.x es muy antiguo. Se recomienda actualizar a Firebird 2.5+ o 3.0+.";
        else if (info.Ods == 13)
            info.Nota = "Firebird 2.1 esta sin soporte oficial. Considera actualizar a 3.0+.";
        else if (info.Ods == 14)
            info.Nota = "Firebird 2.5 esta sin soporte oficial desde 2020. Considera actualizar a 3.0+.";

        return info;
    }

    // ==================================================================
    // ESTRATEGIA 1: LEER ODS DEL HEADER .FDG
    // ==================================================================

    /// <summary>
    /// Lee el ODS (On-Disk Structure) directamente del header de la .fdb.
    /// Funciona con todas las versiones de Firebird sin necesidad de conexion.
    ///
    /// Formato del header page (page_type=0x01):
    ///   Offset 0:  page_type (1 byte) = 0x01
    ///   Offset 1:  checksum flags (1 byte)
    ///   Offset 2-3: checksum (2 bytes)
    ///   Offset 4-7: page size (4 bytes)
    ///   Offset 8-9: ODS major (2 bytes, big-endian)
    ///   Offset 10-11: ODS minor (2 bytes, big-endian)
    ///   ... (resto del header)
    /// </summary>
    public static int LeerOdsDeArchivo(string rutaFdb)
    {
        if (string.IsNullOrWhiteSpace(rutaFdb) || !File.Exists(rutaFdb))
            return 0;

        using var fs = new FileStream(rutaFdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < 16)
            return 0;

        var header = new byte[16];
        fs.ReadExactly(header);

        // Validar page_type = 0x01 (header page)
        if (header[0] != 0x01)
            return 0;

        // ODS major: bytes 8-9 (big-endian en Firebird)
        int odsMajor = (header[8] << 8) | header[9];
        // ODS minor: bytes 10-11 (big-endian)
        int odsMinor = (header[10] << 8) | header[11];

        // Firebird 1.x usa ODS 10.x, 1.5 usa 11.x, etc.
        // El "ODS base" es el numero mayor (10, 11, 12, 13, 14, 15, 16, 17)
        // El minor es la revision dentro de esa version.
        return odsMajor;
    }

    // ==================================================================
    // ESTRATEGIA 2 y 3: QUERIES SQL
    // ==================================================================

    private static void DetectarViaSql(string cadenaConexion, FirebirdVersionInfo info)
    {
        using var conn = new FbConnection(cadenaConexion);
        conn.Open();

        // ---- Intento 1: RDB$GET_CONTEXT (Firebird 3.0+) ----
        // Devuelve "3.0.11" o "4.0.4" etc. Es la forma mas precisa.
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RDB$GET_CONTEXT('SYSTEM', 'ENGINE_VERSION') FROM RDB$DATABASE";
            var result = cmd.ExecuteScalar()?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(result))
            {
                info.Version = result;
                info.Nombre = ExtraerNombreVersion(result);
                info.MetodoDeteccion = "RDB$GET_CONTEXT (ENGINE_VERSION)";
                return;
            }
        }
        catch { /* Firebird < 3.0, no tiene RDB$GET_CONTEXT */ }

        // ---- Intento 2: MON$DATABASE_VERSION (Firebird 2.5+) ----
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MON$DATABASE_VERSION FROM MON$DATABASE";
            var result = cmd.ExecuteScalar()?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(result))
            {
                info.Version = result;
                info.Nombre = ExtraerNombreVersion(result);
                info.MetodoDeteccion = "MON$DATABASE_VERSION";
                return;
            }
        }
        catch { /* Firebird < 2.5, no tiene MON$DATABASE_VERSION */ }

        // ---- Intento 3: solo MON$DATABASE_NAME (Firebird 2.1+) ----
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MON$DATABASE_NAME FROM MON$DATABASE";
            var name = cmd.ExecuteScalar()?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                info.MetodoDeteccion = "MON$DATABASE_NAME (sin version)";
                // No tenemos version, pero sabemos que al menos es 2.1
                if (info.Ods == 0) info.Ods = 13; // asumir 2.1 minimo
            }
        }
        catch { /* Firebird < 2.1 o error */ }
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    /// <summary>
    /// Extrae un nombre amigable de una cadena de version como "3.0.11" o "LI-V3.0.11".
    /// </summary>
    private static string ExtraerNombreVersion(string versionCruda)
    {
        // Limpiar: quitar prefijos como "LI-V", "V", etc.
        var limpia = Regex.Replace(versionCruda, @"^[A-Za-z\-]+", "").Trim();

        // Extraer numeros: "3.0.11" → ["3","0","11"]
        var nums = Regex.Matches(limpia, @"\d+").Select(m => int.Parse(m.Value)).ToArray();

        if (nums.Length >= 2)
        {
            var major = nums[0];
            var minor = nums[1];
            var patch = nums.Length >= 3 ? $".{nums[2]}" : "";
            return $"Firebird {major}.{minor}{patch}";
        }

        return $"Firebird {versionCruda}";
    }
}
