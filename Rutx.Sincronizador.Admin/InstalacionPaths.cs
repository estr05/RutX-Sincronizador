namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Rutas canónicas de la estructura hermana de Microsip que RutX administra.
///
/// Layout aprobado (Sprint 4):
///   C:\Microsip Extras\
///   ├── RUTX_COMPLEMENTARIA.db      ← BD complementaria única
///   ├── Fotos\
///   │   └── NoVentas\               ← fotos completadas
///   │       └── .staging\           ← fotos en tránsito (temporal)
///   ├── Respaldos\                  ← respaldos de BD/C y configuración
///   └── Logs\                       ← logs del servicio en esta ubicación
///
/// C:\Microsip Datos\ NO se toca: es exclusivo de Microsip/Firebird.
/// </summary>
public static class InstalacionPaths
{
    // ────────────────────────────────────────────────────────────────────────
    // Raíz y nombres
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Directorio hermano de Microsip; NO modificar C:\Microsip Datos\.</summary>
    public const string MicrosipExtrasRaiz = @"C:\Microsip Extras";

    /// <summary>Nombre del archivo de BD complementaria.</summary>
    public const string NombreDb = "RUTX_COMPLEMENTARIA.db";

    // ────────────────────────────────────────────────────────────────────────
    // Sub-rutas relativas a MicrosipExtrasRaiz
    // ────────────────────────────────────────────────────────────────────────

    public const string SubRutaFotos     = @"Fotos\NoVentas";
    public const string SubRutaStaging   = @"Fotos\NoVentas\.staging";
    public const string SubRutaRespaldos = "Respaldos";
    public const string SubRutaLogs      = "Logs";

    // ────────────────────────────────────────────────────────────────────────
    // Rutas absolutas derivadas (propiedades para facilitar el uso)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Ruta absoluta de la BD complementaria.</summary>
    public static string RutaDb        => Path.Combine(MicrosipExtrasRaiz, NombreDb);

    /// <summary>Ruta absoluta de la carpeta de fotos finalizadas.</summary>
    public static string RutaFotos     => Path.Combine(MicrosipExtrasRaiz, SubRutaFotos);

    /// <summary>Ruta absoluta de la carpeta de staging (fotos en tránsito).</summary>
    public static string RutaStaging   => Path.Combine(MicrosipExtrasRaiz, SubRutaStaging);

    /// <summary>Ruta absoluta de la carpeta de respaldos.</summary>
    public static string RutaRespaldos => Path.Combine(MicrosipExtrasRaiz, SubRutaRespaldos);

    /// <summary>Ruta absoluta de la carpeta de logs de Microsip Extras.</summary>
    public static string RutaLogs      => Path.Combine(MicrosipExtrasRaiz, SubRutaLogs);

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Devuelve la ruta de un archivo de respaldo con marca de tiempo UTC.
    /// Ejemplo: C:\Microsip Extras\Respaldos\RUTX_COMPLEMENTARIA_20260819T220000Z.db.bak
    /// </summary>
    public static string GenerarRutaRespaldo(string nombreArchivo)
    {
        var ts  = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var ext = Path.GetExtension(nombreArchivo);
        var sin = Path.GetFileNameWithoutExtension(nombreArchivo);
        return Path.Combine(RutaRespaldos, $"{sin}_{ts}{ext}.bak");
    }
}
