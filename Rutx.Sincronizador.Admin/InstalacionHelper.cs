using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using System.Security.Principal;

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
///   ├── appsettings.json        (config sin contraseña real)
///   ├── Logs\                   (bitacora de la raiz del exe)
///   ├── backups\                (respaldos .bak del panel web)
///   └── instalacion.json        (marcador leido por el launcher)
///
/// La BD complementaria (RUTX_COMPLEMENTARIA.db) y las fotos viven en
/// C:\Microsip Extras\ (ver InstalacionPaths). El exe no escribe dentro
/// de C:\Microsip Datos\ (exclusivo de Firebird/Microsip).
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

    // Subcarpetas que se crean en la raiz (junto al exe).
    // "Data" se elimina: la BD complementaria vive en C:\Microsip Extras\
    private static readonly string[] CarpetasRaiz = { "wwwroot", "Logs", "backups" };

    // Archivos que NO se copian del folder de build a la raiz
    private static readonly string[] ExcluirPatrones =
    {
        "Data",                  // BD complementaria: vive en Microsip Extras
        "wwwroot",               // se copia aparte a la raiz
        "appsettings",           // se genera fresco en la raiz
        "auditoria_resultado",   // resultado previo, irrelevante
    };

    private static readonly string[] ExcluirExtensiones = { ".pdb", ".xml", ".db", ".db-shm", ".db-wal" };

    /// <summary>
    /// Ejecuta la instalacion completa. Devuelve la InstalacionInfo creada.
    /// Lanza excepcion con mensaje claro si algo falla a mitad (para que el
    /// wizard lo muestre y no deje una instalacion a medias sin avisar).
    ///
    /// SEGURIDAD: la contraseña se usa SOLO para la prueba de conexion en el
    /// wizard. El appsettings.json generado nunca contiene la contraseña real;
    /// debe proveerla en produccion mediante la variable de entorno
    /// ConnectionStrings__FirebirdConnection (ver CONFIGURACION_PRODUCCION.md).
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

        // ---- 2. Crear estructura del exe ----
        Directory.CreateDirectory(raiz);
        foreach (var carpeta in CarpetasRaiz)
            Directory.CreateDirectory(Path.Combine(raiz, carpeta));

        // ---- 3. Crear estructura de C:\Microsip Extras\ (idempotente) ----
        CrearEstructuraMicrosipExtras();

        // ---- 4. Migrar archivos legacy si existen (no borrar, solo respaldar) ----
        MigrarBdcLegada(raiz);

        // ---- 5. Copiar ejecutables (exe + DLLs) directo a la raiz ----
        CopiarDirectorio(carpetaFuenteSync, raiz);

        // ---- 6. Copiar wwwroot a la raiz ----
        var wwwrootOrigen = Path.Combine(carpetaFuenteSync, "wwwroot");
        var wwwrootDestino = Path.Combine(raiz, "wwwroot");
        if (Directory.Exists(wwwrootOrigen))
            CopiarDirectorio(wwwrootOrigen, wwwrootDestino);

        // ---- 7. Generar appsettings.json en la raiz ----
        // NOTA: la contraseña real NO se persiste; debe configurarse con la
        // variable de entorno ConnectionStrings__FirebirdConnection en produccion.
        var plantilla = Path.Combine(carpetaFuenteSync, "appsettings.json");
        if (!File.Exists(plantilla))
            throw new InvalidOperationException(
                "No se encontro la plantilla appsettings.json en: " + plantilla);

        var rutaAppSettings = Path.Combine(raiz, "appsettings.json");
        EscribirAppSettings(plantilla, rutaAppSettings, rutaFdb, usuario);

        // ---- 8. Marcador instalacion.json ----
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
    // ESTRUCTURA MICROSIP EXTRAS (idempotente)
    // ==================================================================

    /// <summary>
    /// Crea la estructura completa de C:\Microsip Extras\ si no existe.
    /// Idempotente: Directory.CreateDirectory no falla si ya existe.
    /// No modifica ni elimina archivos existentes.
    /// </summary>
    public static void CrearEstructuraMicrosipExtras()
    {
        Directory.CreateDirectory(InstalacionPaths.MicrosipExtrasRaiz);
        Directory.CreateDirectory(InstalacionPaths.RutaFotos);
        Directory.CreateDirectory(InstalacionPaths.RutaStaging);
        Directory.CreateDirectory(InstalacionPaths.RutaRespaldos);
        Directory.CreateDirectory(InstalacionPaths.RutaLogs);

        EstablecerAcls(InstalacionPaths.MicrosipExtrasRaiz);
    }

    private static void EstablecerAcls(string directorio)
    {
        if (!OperatingSystem.IsWindows()) return;
        
        try
        {
            var dInfo = new DirectoryInfo(directorio);
            var security = dInfo.GetAccessControl();
            
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);

            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            
            dInfo.SetAccessControl(security);
            Console.WriteLine($"[INFO] ACLs aplicadas en {directorio} (Administrators, SYSTEM, NetworkService)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AVISO] No se pudieron aplicar las ACLs en {directorio}: {ex.Message}");
        }
    }

    /// <summary>
    /// Detecta archivos de BD legacy (Data/web.db, Data/cola_offline.db) junto
    /// al exe anterior y crea un respaldo .bak en Respaldos\ antes de cualquier
    /// accion. NO los elimina (se mantienen como respaldo hasta sprint posterior).
    /// </summary>
    private static void MigrarBdcLegada(string raizExe)
    {
        var candidatos = new[]
        {
            Path.Combine(raizExe, "Data", "web.db"),
            Path.Combine(raizExe, "Data", "cola_offline.db"),
        };

        foreach (var archivo in candidatos)
        {
            if (!File.Exists(archivo)) continue;

            var destBak = InstalacionPaths.GenerarRutaRespaldo(Path.GetFileName(archivo));
            try
            {
                File.Copy(archivo, destBak, overwrite: false);
                // Registro en consola (el wizard captura StdOut para mostrarlo)
                Console.WriteLine($"[INFO] Respaldo creado: {destBak}");
                Console.WriteLine($"[INFO] Archivo legacy conservado: {archivo}");
                Console.WriteLine("[INFO] La fusion de datos se realizara en la proxima actualizacion.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AVISO] No se pudo respaldar {archivo}: {ex.Message}");
            }
        }
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

    /// <summary>
    /// Genera appsettings.json en la raiz de instalacion.
    /// SEGURIDAD: la contraseña NUNCA se escribe en el archivo generado.
    /// El placeholder CHANGE_ME_FIREBIRD_PASSWORD debe sustituirse en produccion
    /// con la variable de entorno ConnectionStrings__FirebirdConnection.
    /// </summary>
    private static void EscribirAppSettings(string plantilla, string destino,
        string rutaFdb, string usuario)
    {
        var nodo = JsonNode.Parse(File.ReadAllText(plantilla))
                   ?? throw new InvalidOperationException("La plantilla appsettings.json no es JSON valido.");

        var conexiones = nodo["ConnectionStrings"] as JsonObject;
        if (conexiones == null)
            throw new InvalidOperationException("La plantilla no tiene la seccion ConnectionStrings.");

        // Construir la cadena con placeholder de contraseña (nunca la contraseña real).
        // Produccion debe sobreescribir via variable de entorno.
        var cadena = FbConexionHelper.ConstruirCadena(rutaFdb, usuario, "CHANGE_ME_FIREBIRD_PASSWORD");
        cadena = cadena.Replace("Pooling=False", "Pooling=True");

        conexiones["FirebirdConnection"] = cadena;

        // Actualizar ruta SQLite al path canonico de Microsip Extras (Consolidacion Fase B)
        var comple = nodo["ComplementariaDb"] as JsonObject;
        if (comple == null)
        {
            comple = new JsonObject();
            nodo["ComplementariaDb"] = comple;
        }
        comple["Ruta"] = InstalacionPaths.RutaDb;

        // Limpiar configuraciones legadas
        nodo.AsObject().Remove("WebSqlite");
        nodo.AsObject().Remove("ColaOffline");

        var storage = nodo["Storage"] as JsonObject;
        if (storage != null)
            storage["FotosPath"] = InstalacionPaths.RutaFotos;

        File.WriteAllText(destino, nodo.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
