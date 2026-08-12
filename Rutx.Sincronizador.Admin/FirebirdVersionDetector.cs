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

    /// <summary>ODS major (On-Disk Structure) leido del header de la .fdb.</summary>
    public int Ods { get; set; }

    /// <summary>ODS minor del header de la .fdb.</summary>
    public int OdsMinor { get; set; }

    /// <summary>Nombre descriptivo (ej. "Firebird 3.0", "Firebird 5.0").</summary>
    public string Nombre { get; set; } = "";

    /// <summary>True si es una version soportada para Microsip (>= 2.1).</summary>
    public bool Soportada { get; set; }

    /// <summary>Advertencias o recomendaciones para esta version.</summary>
    public string? Nota { get; set; }

    /// <summary>Como se detecto (ODS, RDB$GET_CONTEXT, MON$, etc.).</summary>
    public string MetodoDeteccion { get; set; } = "";

    public override string ToString() =>
        string.IsNullOrEmpty(Version)
            ? $"{Nombre} (ODS {Ods}.{OdsMinor}) — version desconocida"
            : $"{Nombre} (ODS {Ods}.{OdsMinor}) — {Version}";
}

/// <summary>
/// Detecta la version de Firebird usando multiples estrategias:
///   1. ODS del header .fdb (sin conexion, todas las versiones)
///   2. RDB$GET_CONTEXT('SYSTEM','ENGINE_VERSION') (Firebird 2.1+, mas preciso)
///   3. MON$ODS_MAJOR / MON$ODS_MINOR (Firebird 2.1+)
///   4. Fallback: solo MON$DATABASE_NAME (Firebird 2.1+)
///
/// 100% SOLO LECTURA — nunca escribe en la BD.
/// </summary>
public static class FirebirdVersionDetector
{
    // ==================================================================
    // MAPA ODS → FIREBIRD VERSION
    // ==================================================================
    // ODS = On-Disk Structure. El header page de la .fdb guarda el ODS
    // mayor en los bytes 18-19 (little-endian, con el flag Firebird 0x8000)
    // y el ODS menor en un offset que depende de la familia (ver
    // LeerOdsDeArchivo). Se lee sin abrir conexion.
    //
    // Fuente: ods.h de Firebird (B1_5/B2_0/B2_5/B3_0/master) + gstat
    // verificado contra BDs reales:
    //   ODS 10.0 = Firebird 1.0 (tambien InterBase 6.0)
    //   ODS 10.1 = Firebird 1.5
    //   ODS 11.0 = Firebird 2.0
    //   ODS 11.1 = Firebird 2.1
    //   ODS 11.2 = Firebird 2.5
    //   ODS 12.0 = Firebird 3.0
    //   ODS 13.0 = Firebird 4.0
    //   ODS 13.1 = Firebird 5.0
    // ==================================================================

    /// <summary>
    /// Nombre amigable de una combinacion ODS (ej. (13, 1) -> "Firebird 5.0").
    /// Con minor no mapeado usa la familia mas nueva del major (ej. ODS 13.3 -> 5.0).
    /// La usa el clasificador de errores de conexion para explicar desajustes
    /// entre la version de la BD y la del servidor local.
    /// </summary>
    public static string NombreFirebirdDeOds(int major, int minor)
    {
        if (OdsMap.TryGetValue((major, minor), out var m))
            return m.Nombre;

        var candidatos = OdsMap
            .Where(kv => kv.Key.Major == major)
            .Select(kv => kv.Value)
            .OrderByDescending(v => v.Version)
            .ToList();
        if (candidatos.Count > 0)
            return candidatos[0].Nombre;

        return $"Firebird (ODS {major}.{minor})";
    }

    // Mapa por (major, minor) → version. Los majors son 10, 11, 12, 13...
    private static readonly Dictionary<(int Major, int Minor), (string Version, string Nombre)> OdsMap = new()
    {
        [(10, 0)] = ("1.0",   "Firebird 1.0"),
        [(10, 1)] = ("1.5",   "Firebird 1.5"),
        [(11, 0)] = ("2.0",   "Firebird 2.0"),
        [(11, 1)] = ("2.1",   "Firebird 2.1"),
        [(11, 2)] = ("2.5",   "Firebird 2.5"),
        [(12, 0)] = ("3.0",   "Firebird 3.0"),
        [(13, 0)] = ("4.0",   "Firebird 4.0"),
        [(13, 1)] = ("5.0",   "Firebird 5.0"),
    };

    // Familia minima soportada para Microsip: ODS major 11 = Firebird 2.x.
    // Debajo de eso (Firebird 1.x) la app movil no puede operar bien.
    private const int OdsMajorMinimoSoportado = 11;

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
            var (ods, odsMinor) = LeerOdsDeArchivo(rutaFdb);
            if (ods > 0)
            {
                info.Ods = ods;
                info.OdsMinor = odsMinor;
                info.MetodoDeteccion = "ODS (header .fdb)";
            }
        }
        catch { /* no se pudo leer el header */ }

        // ---- Estrategia 2 y 3: queries SQL (requiere conexion) ----
        if (!string.IsNullOrEmpty(cadenaConexion))
        {
            try
            {
                using var conn = new FbConnection(cadenaConexion);
                conn.Open();
                DetectarViaSql(conn, info);
            }
            catch { /* la conexion fallo, usamos lo que tengamos del ODS */ }
        }

        // ---- Completar campos faltantes a partir del ODS ----
        CompletarDesdeOds(info);

        return info;
    }

    /// <summary>
    /// Ejecuta la deteccion SQL sobre una conexion YA abierta y con credenciales
    /// validas (la usa el wizard de instalacion para no abrir una segunda conexion
    /// y para no intentar SQL cuando las credenciales aun no conectan).
    /// </summary>
    public static void DetectarVersionSql(FbConnection conexionAbierta, FirebirdVersionInfo info)
    {
        if (conexionAbierta?.State != System.Data.ConnectionState.Open)
            return;

        DetectarViaSql(conexionAbierta, info);
        CompletarDesdeOds(info);
    }

    // ==================================================================
    // ESTRATEGIA 1: LEER ODS DEL HEADER .FDB
    // ==================================================================

    /// <summary>
    /// Lee el ODS (On-Disk Structure) directamente del header de la .fdb.
    /// Funciona con todas las versiones de Firebird sin necesidad de conexion.
    ///
    /// Layout del header page (type 0x01), segun ods.h de Firebird:
    ///   Offset  0: page_type (1 byte) = 0x01
    ///   Offset  1: flags (1 byte)
    ///   Offset  2-3: checksum (2 bytes, little-endian)
    ///   Offset 16-17: hdr_page_size (2 bytes, little-endian)
    ///   Offset 18-19: hdr_ods_version (2 bytes, little-endian)
    ///                 = ODS major AND 0x8000 (flag Firebird)
    ///
    /// El ODS minor NO esta en un offset fijo; el layout del header cambio
    /// entre familias (verificado contra ods.h de cada version + gstat):
    ///   - ODS <= 11 (Firebird 1.x y 2.x): hdr_ods_minor en offset 62
    ///               (en 64 esta hdr_ods_minor_original, el minor al crearse;
    ///                en una BD actualizada 2.1 -> 2.5, 64 sigue valiendo 1)
    ///   - ODS 12 y 13 (Firebird 3.0, 4.0 y 5.0): hdr_ods_minor en offset 64
    ///
    /// NOTA: el layout con hdr_ods_minor en offset 20 existe solo en el branch
    /// master de Firebird (6.0 en desarrollo); NINGUNA version publicada lo usa.
    ///
    /// Devuelve (major, minor) o (0, 0) si no es un header valido.
    /// </summary>
    public static (int Major, int Minor) LeerOdsDeArchivo(string rutaFdb)
    {
        if (string.IsNullOrWhiteSpace(rutaFdb) || !File.Exists(rutaFdb))
            return (0, 0);

        using var fs = new FileStream(rutaFdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < 66)
            return (0, 0);

        var header = new byte[66];
        fs.ReadExactly(header);

        // Validar page_type = 0x01 (header page)
        if (header[0] != 0x01)
            return (0, 0);

        // ODS major: bytes 18-19, little-endian, con flag Firebird 0x8000.
        // Ej: 0x800B → major 11 (Firebird 2.x), 0x800C → major 12 (Firebird 3.0),
        //     0x800D → major 13 (Firebird 4.0/5.0).
        ushort odsRaw = (ushort)(header[18] | (header[19] << 8));
        int odsMajor = odsRaw & 0x7FFF;  // quitar el flag Firebird

        // Offset del ODS minor segun la familia (ver comentario del metodo):
        //   ODS <= 11 -> 62 (Firebird 1.x/2.x)
        //   ODS 12 y 13 -> 64 (Firebird 3.0/4.0/5.0)
        int offsetMinor = odsMajor <= 11 ? 62 : 64;
        int odsMinor = header[offsetMinor] | (header[offsetMinor + 1] << 8);

        return (odsMajor, odsMinor);
    }

    // ==================================================================
    // ESTRATEGIA 2, 3 y 4: QUERIES SQL
    // ==================================================================

    private static void DetectarViaSql(FbConnection conn, FirebirdVersionInfo info)
    {
        // ---- Intento 1: RDB$GET_CONTEXT (Firebird 2.1+) ----
        // Devuelve "3.0.11", "2.5.9", "4.0.4" etc. Es la forma mas precisa.
        // (ENGINE_VERSION se agrego en Firebird 2.1; en 2.0 y 1.x no existe.)
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
        catch { /* Firebird < 2.1, no tiene ENGINE_VERSION */ }

        // ---- Intento 2: MON$ODS_MAJOR / MON$ODS_MINOR (Firebird 2.1+) ----
        // NOTA: MON$DATABASE_VERSION NO existe en ninguna version de Firebird;
        // el motor expone el ODS real a traves de MON$ODS_MAJOR/MON$ODS_MINOR.
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MON$ODS_MAJOR, MON$ODS_MINOR FROM MON$DATABASE";
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read())
            {
                var major = Convert.ToInt32(rdr[0]);
                var minor = Convert.ToInt32(rdr[1]);
                if (major > 0)
                {
                    info.Ods = major;
                    info.OdsMinor = minor;
                }
                // Solo se marca como origen MON$ si el header del archivo no dio ODS
                // (si el archivo ya lo dio, ese sigue siendo el origen del dato).
                if (info.Ods == 0 && string.IsNullOrEmpty(info.MetodoDeteccion))
                    info.MetodoDeteccion = "MON$ (ODS del motor)";
            }
        }
        catch { /* Firebird < 2.1, no tiene tablas MON$ */ }

        // ---- Intento 3: solo MON$DATABASE_NAME (Firebird 2.1+) ----
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MON$DATABASE_NAME FROM MON$DATABASE";
            var name = cmd.ExecuteScalar()?.ToString()?.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                if (string.IsNullOrEmpty(info.MetodoDeteccion) ||
                    info.MetodoDeteccion.StartsWith("ODS", StringComparison.Ordinal))
                    info.MetodoDeteccion = "MON$DATABASE_NAME (sin version)";
                // No tenemos version exacta, pero sabemos que al menos es 2.1
                if (info.Ods == 0)
                {
                    info.Ods = 11;
                    info.OdsMinor = 1;
                    info.Nombre = "Firebird 2.1+";
                }
            }
        }
        catch { /* Firebird < 2.1 o error */ }
    }

    // ==================================================================
    // COMPLETAR CAMPOS A PARTIR DEL ODS
    // ==================================================================

    private static void CompletarDesdeOds(FirebirdVersionInfo info)
    {
        if (info.Ods > 0)
        {
            var exacta = OdsMap.TryGetValue((info.Ods, info.OdsMinor), out var m0);
            (string Version, string Nombre)? porMajor = null;
            if (!exacta)
            {
                // Minor no mapeado exacto: usar la version mas nueva de la familia.
                // Ej: ODS 13.3 -> Firebird 5.0 (no 4.0).
                var candidatos = OdsMap
                    .Where(kv => kv.Key.Major == info.Ods)
                    .Select(kv => kv.Value)
                    .OrderByDescending(v => v.Version)
                    .ToList();
                if (candidatos.Count > 0)
                    porMajor = candidatos[0];
            }

            if (exacta && string.IsNullOrEmpty(info.Version))
            {
                info.Version = m0.Version;
                info.Nombre = m0.Nombre;
            }
            else if (porMajor is { } m1 && string.IsNullOrEmpty(info.Version))
            {
                // ODS major conocido pero minor desconocido: mostrar familia
                // (ej. ODS 13.3 -> Firebird 5.0; ODS 12.2 -> Firebird 3.0)
                info.Nombre = m1.Nombre;
            }

            if (string.IsNullOrEmpty(info.Nombre))
                info.Nombre = $"Firebird (ODS {info.Ods}.{info.OdsMinor})";
        }

        if (string.IsNullOrEmpty(info.Nombre) && !string.IsNullOrEmpty(info.Version))
            info.Nombre = ExtraerNombreVersion(info.Version);

        // Soportada? (0 = no detectado via ODS, asumir ok si hay version SQL)
        if (info.Ods == 0)
            info.Soportada = !string.IsNullOrEmpty(info.Version);
        else
            info.Soportada = info.Ods >= OdsMajorMinimoSoportado;

        // Notas para versiones problematicas
        if (info.Ods == 10)
            info.Nota = "Firebird 1.x es muy antiguo. Se recomienda actualizar a Firebird 2.5+ o 3.0+.";
        else if (info.Ods == 11 && info.OdsMinor == 0)
            info.Nota = "Firebird 2.0 es muy antiguo. Considera actualizar a 3.0+.";
        else if (info.Ods == 11 && info.OdsMinor == 1)
            info.Nota = "Firebird 2.1 esta sin soporte oficial. Considera actualizar a 3.0+.";
        else if (info.Ods == 11 && info.OdsMinor == 2)
            info.Nota = "Firebird 2.5 esta sin soporte oficial desde 2020. Considera actualizar a 3.0+.";
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    /// <summary>
    /// Extrae un nombre amigable de una cadena de version como "3.0.11" o "LI-V3.0.11".
    /// </summary>
    private static string ExtraerNombreVersion(string versionCruda)
    {
        // Limpiar: quitar prefijos como "LI-V", "WI-V", "V", etc.
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
