using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Informacion de la instalacion estandarizada guardada en instalacion.json
/// (en la raiz de instalacion). Permite al launcher saber donde esta el sync
/// y usar la raiz como WorkingDirectory (para que appsettings/wwwroot/Data
/// caigan en la raiz, no junto al exe).
/// </summary>
public class InstalacionInfo
{
    public string Raiz { get; set; } = "";
    public string ExeSync { get; set; } = "";
    public string ExeAdmin { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string BdPath { get; set; } = "";
    public string UsuarioFb { get; set; } = "";
}

/// <summary>
/// Crea la estructura plana de instalacion del sincronizador:
///   C:\ProgramData\RUTX\Sincronizador\
///   ├── Rutx.Sincronizador.exe  (y DLLs del sync)
///   ├── wwwroot\                (panel /admin)
///   ├── appsettings.json        (config: BD, usuario, password, IDs)
///   ├── Data\                   (cola SQLite — se auto-crea tambien)
///   ├── Logs\                   (bitacora opcional)
///   ├── backups\                (respaldos .bak del panel web)
///   └── instalacion.json        (marcador leido por el launcher)
///
/// Layout plano: ContentRootPath = AppContext.BaseDirectory = raiz.
/// El exe, appsettings y wwwroot viven juntos en la raiz.
/// </summary>
public static class InstalacionHelper
{
    public const string NombreArchivoMarcador = "instalacion.json";
    public static readonly string RutaDefault = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RUTX", "Sincronizador");

    // Subcarpetas que se crean en la raiz (junto al exe)
    private static readonly string[] CarpetasRaiz = { "wwwroot", "Data", "Logs", "backups" };

    // Archivos que NO se copian del folder de build a la raiz
    private static readonly string[] ExcluirPatrones =
    {
        "Data",                  // cola SQLite: es por-instalacion
        "wwwroot",               // se copia aparte a la raiz
        "appsettings",           // se genera fresco en la raiz
        "auditoria_resultado",   // resultado previo, irrelevante
    };

    private static readonly string[] ExcluirExtensiones = { ".pdb", ".xml", ".db", ".db-shm", ".db-wal" };

    /// <summary>
    /// Ejecuta la instalacion completa. Devuelve la InstalacionInfo creada.
    /// Lanza excepcion con mensaje claro si algo falla a mitad (para que el
    /// wizard lo muestre y no deje una instalacion a medias sin avisar).
    /// </summary>
    public static InstalacionInfo Instalar(string raiz, string rutaFdb, string usuario, string password,
        string carpetaFuenteSync)
    {
        // ---- 1. Validar origen y destino ----
        if (string.IsNullOrWhiteSpace(carpetaFuenteSync) || !Directory.Exists(carpetaFuenteSync))
            throw new InvalidOperationException(
                "No se encontro la carpeta del sincronizador a copiar: " + carpetaFuenteSync);

        var exeOrigen = Path.Combine(carpetaFuenteSync, "Rutx.Sincronizador.exe");
        if (!File.Exists(exeOrigen))
            throw new InvalidOperationException(
                "No se encontro Rutx.Sincronizador.exe en la carpeta fuente: " + exeOrigen);

        raiz = Path.GetFullPath(raiz.Trim());
        if (string.IsNullOrWhiteSpace(Path.GetPathRoot(raiz)))
            throw new InvalidOperationException("La ruta de instalacion no es valida: " + raiz);

        // ---- 2. Crear estructura ----
        Directory.CreateDirectory(raiz);
        foreach (var carpeta in CarpetasRaiz)
            Directory.CreateDirectory(Path.Combine(raiz, carpeta));

        // ---- 3. Copiar ejecutables (exe + DLLs) directo a la raiz ----
        CopiarDirectorio(carpetaFuenteSync, raiz);

        // ---- 4. Copiar wwwroot a la raiz ----
        var wwwrootOrigen = Path.Combine(carpetaFuenteSync, "wwwroot");
        var wwwrootDestino = Path.Combine(raiz, "wwwroot");
        if (Directory.Exists(wwwrootOrigen))
            CopiarDirectorio(wwwrootOrigen, wwwrootDestino);

        // ---- 5. Generar appsettings.json en la raiz (plantilla + BD elegida) ----
        var plantilla = Path.Combine(carpetaFuenteSync, "appsettings.json");
        if (!File.Exists(plantilla))
            throw new InvalidOperationException(
                "No se encontro la plantilla appsettings.json en: " + plantilla);

        var rutaAppSettings = Path.Combine(raiz, "appsettings.json");
        EscribirAppSettings(plantilla, rutaAppSettings, rutaFdb, usuario, password);

        // ---- 6. Marcador instalacion.json ----
        var info = new InstalacionInfo
        {
            Raiz = raiz,
            ExeSync = Path.Combine(raiz, "Rutx.Sincronizador.exe"),
            ExeAdmin = Path.Combine(AppContext.BaseDirectory, "Rutx.Sincronizador.Admin.exe"),
            Fecha = DateTime.Now,
            BdPath = Path.GetFullPath(rutaFdb),
            UsuarioFb = usuario
        };
        var rutaMarcador = Path.Combine(raiz, NombreArchivoMarcador);
        File.WriteAllText(rutaMarcador, JsonSerializer.Serialize(info,
            new JsonSerializerOptions { WriteIndented = true }));

        return info;
    }

    /// <summary>
    /// Lee el marcador de instalacion si existe (y el exe sigue existiendo).
    /// Devuelve null si no hay instalacion valida.
    /// </summary>
    public static InstalacionInfo? LeerInstalacion(string? raiz = null)
    {
        var ruta = string.IsNullOrWhiteSpace(raiz)
            ? Path.Combine(RutaDefault, NombreArchivoMarcador)
            : Path.Combine(raiz, NombreArchivoMarcador);

        if (!File.Exists(ruta))
            return null;

        try
        {
            var info = JsonSerializer.Deserialize<InstalacionInfo>(File.ReadAllText(ruta));
            if (info == null || string.IsNullOrWhiteSpace(info.ExeSync) || !File.Exists(info.ExeSync))
                return null;
            return info;
        }
        catch
        {
            return null; // marcador corrupto -> se ignora
        }
    }

    /// <summary>
    /// Busca la instalacion estandarizada. Si hay mas de una (marcadores en
    /// otras carpetas), se prioriza la default de ProgramData.
    /// </summary>
    public static InstalacionInfo? BuscarInstalacion()
    {
        var enDefault = LeerInstalacion();
        if (enDefault != null) return enDefault;

        // Busqueda de respaldo: maracadores instalacion.json en subcarpetas de ProgramData\RUTX
        try
        {
            var baseRutx = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RUTX");
            if (!Directory.Exists(baseRutx)) return null;

            foreach (var dir in Directory.EnumerateDirectories(baseRutx, "*", SearchOption.AllDirectories))
            {
                var info = LeerInstalacion(dir);
                if (info != null) return info;
            }
        }
        catch
        {
            // sin permisos de lectura: no hay instalacion detectable
        }
        return null;
    }

    // ==================================================================
    // HELPERS PRIVADOS
    // ==================================================================

    private static void CopiarDirectorio(string origen, string destino)
    {
        Directory.CreateDirectory(destino);

        foreach (var dir in Directory.EnumerateDirectories(origen))
        {
            var nombre = Path.GetFileName(dir);
            if (ExcluirPatrones.Any(p => nombre.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            CopiarDirectorio(dir, Path.Combine(destino, nombre));
        }

        foreach (var archivo in Directory.EnumerateFiles(origen))
        {
            var ext = Path.GetExtension(archivo).ToLowerInvariant();
            if (ExcluirExtensiones.Contains(ext))
                continue;
            if (ExcluirPatrones.Any(p =>
                    Path.GetFileName(archivo).StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;
            File.Copy(archivo, Path.Combine(destino, Path.GetFileName(archivo)), overwrite: true);
        }
    }

    private static void EscribirAppSettings(string plantilla, string destino,
        string rutaFdb, string usuario, string password)
    {
        var nodo = JsonNode.Parse(File.ReadAllText(plantilla))
                   ?? throw new InvalidOperationException("La plantilla appsettings.json no es JSON valido.");

        var conexiones = nodo["ConnectionStrings"] as JsonObject;
        if (conexiones == null)
            throw new InvalidOperationException("La plantilla no tiene la seccion ConnectionStrings.");

        // Cadena Firebird con los datos elegidos (DataSource localhost/3050)
        var cadena = FbConexionHelper.ConstruirCadena(rutaFdb, usuario, password);

        // Pooling=true en produccion (la prueba de instalacion uso Pooling=false)
        cadena = cadena.Replace("Pooling=False", "Pooling=True");

        conexiones["FirebirdConnection"] = cadena;

        File.WriteAllText(destino, nodo.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
