// ============================================================================
// ARCHIVO: FirebirdVersionDetector.cs (proyecto web Rutx.Sincronizador)
// PROPOSITO: Detecta la version de Firebird de una .fdb leyendo el header del
//            archivo (On-Disk Structure, ODS). Es la MISMA logica que usa
//            Rutx.Sincronizador.Admin.FirebirdVersionDetector (proyecto .exe
//            separado, no referenciable desde aqui).
//
// Sin conexion y 100% SOLO LECTURA: funciona para TODAS las versiones
// (Firebird 1.0 a 5.0) e incluso cuando el servidor local NO puede abrir la
// BD (ej. BD Firebird 5.0 ODS 13.1 con servidor Firebird 3.0) — justo el caso
// donde mas se necesita saber la version.
//
// Layout del header page (type 0x01), segun ods.h de Firebird:
//   Offset 18-19: hdr_ods_version (little-endian, AND 0x8000 = flag Firebird)
//   ODS minor: offset 62 para ODS <= 11 (FB 1.x/2.x), offset 64 para ODS 12+.
// ============================================================================

namespace Rutx.Sincronizador.Services;

public static class FirebirdVersionDetector
{
    // Mapa (major, minor) -> nombre. Verificado contra ods.h de cada version
    // y contra BDs reales (CHOCOLATES 12.0, CRUZROJASCLC 13.1, security2 11.2).
    private static readonly Dictionary<(int Major, int Minor), string> OdsMap = new()
    {
        [(10, 0)] = "Firebird 1.0",
        [(10, 1)] = "Firebird 1.5",
        [(11, 0)] = "Firebird 2.0",
        [(11, 1)] = "Firebird 2.1",
        [(11, 2)] = "Firebird 2.5",
        [(12, 0)] = "Firebird 3.0",
        [(13, 0)] = "Firebird 4.0",
        [(13, 1)] = "Firebird 5.0",
    };

    /// <summary>
    /// Nombre amigable de una combinacion ODS (ej. (13, 1) -> "Firebird 5.0").
    /// Con un minor no mapeado usa la familia mas nueva del major (ej. ODS 13.3 -> 5.0).
    /// </summary>
    public static string NombreFirebirdDeOds(int major, int minor)
    {
        if (OdsMap.TryGetValue((major, minor), out var nombre))
            return nombre;

        var candidatos = OdsMap
            .Where(kv => kv.Key.Major == major)
            .Select(kv => kv.Value)
            .OrderByDescending(v => v)
            .ToList();
        if (candidatos.Count > 0)
            return candidatos[0];

        return $"Firebird (ODS {major}.{minor})";
    }

    /// <summary>
    /// Lee el ODS (major, minor) del header de la .fdb. Devuelve (0, 0) si no
    /// se puede leer (archivo inexistente, no es header valido, etc.).
    /// </summary>
    public static (int Major, int Minor) LeerOdsDeArchivo(string rutaFdb)
    {
        if (string.IsNullOrWhiteSpace(rutaFdb) || !File.Exists(rutaFdb))
            return (0, 0);

        try
        {
            using var fs = new FileStream(rutaFdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 66)
                return (0, 0);

            var header = new byte[66];
            fs.ReadExactly(header, 0, 66);

            // Byte 0: page_type = 0x01 (header page)
            if (header[0] != 1)
                return (0, 0);

            int major = BitConverter.ToUInt16(header, 18) & 0x7FFF;
            // ODS <= 11 (FB 1.x/2.x): minor en offset 62; ODS 12+ (FB 3.0+): 64
            int offsetMinor = major <= 11 ? 62 : 64;
            int minor = BitConverter.ToUInt16(header, offsetMinor);
            return (major, minor);
        }
        catch
        {
            return (0, 0);
        }
    }
}
