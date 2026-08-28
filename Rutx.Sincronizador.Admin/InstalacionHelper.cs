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
    private static readonly string[] CarpetasRaiz = { "Logs", "backups" };

    // Archivos/Carpetas que NO se copian del folder de build a la raiz
    private static readonly string[] ExcluirPatrones =
    {
        "Data",                  // BD complementaria: vive en Microsip Extras
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
        string carpetaFuenteSync, bool mobileRemoteAccess = false, string webPassword = "", string? publicHostname = null)
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

        // ---- 1b. Validacion previa de credenciales y hostname (SIN efectos
        // secundarios): se comprueba TODO antes de crear directorios, aplicar
        // ACLs o copiar archivos, para no dejar una instalacion a medias. ----
        var panelUser = Rutx.Sincronizador.Shared.WebPasswordPolicy.UsuarioPanelDefault;
        if (!Rutx.Sincronizador.Shared.WebPasswordPolicy.EsValida(webPassword, panelUser, out var passError))
            throw new InvalidOperationException(passError);

        var hostnameSaneado = Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.Sanitizar(publicHostname);
        if (mobileRemoteAccess &&
            !Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.EsFqdnValido(hostnameSaneado, out var hostError))
        {
            throw new InvalidOperationException(hostError);
        }

        // ---- 2. Pre-check: Matar proceso si el ejecutable destino esta bloqueado ----
        var exeDestino = Path.Combine(raiz, "Rutx.Sincronizador.exe");
        if (File.Exists(exeDestino))
        {
            try
            {
                using var fs = new FileStream(exeDestino, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Console.WriteLine("[INFO] El sincronizador esta bloqueado en el destino. Deteniendolo antes de copiar...");
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("Rutx.Sincronizador"))
                {
                    try
                    {
                        // Ensure we don't kill our own launcher process if somehow names matched (though launcher is .Admin)
                        if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                        {
                            p.Kill();
                            p.WaitForExit(3000);
                        }
                    }
                    catch { /* ignore */ }
                }
            }
        }

        // ---- 3. Crear estructura del exe ----
        Directory.CreateDirectory(raiz);
        foreach (var carpeta in CarpetasRaiz)
            Directory.CreateDirectory(Path.Combine(raiz, carpeta));

        // Aplicar ACLs heredables a la raiz de instalacion:
        // El subproceso del sync (hijo del wizard) necesita poder ESCRIBIR en Logs\ y backups\.
        // C:\ProgramData\ solo da ReadAndExecute a Users por defecto, lo que bloquearía al sync.
        var (aclsRaizOk, aclsRaizMsj) = EstablecerAclsRaizInstalacion(raiz);
        if (!aclsRaizOk)
            Console.WriteLine($"[AVISO] {aclsRaizMsj}");
        else
            Console.WriteLine($"[INFO] {aclsRaizMsj}");

        // ---- 4. Crear estructura de C:\Microsip Extras\ (idempotente) ----
        var (aclsOk, aclMsj) = CrearEstructuraMicrosipExtras();
        if (!aclsOk)
            Console.WriteLine($"[AVISO] {aclMsj}");
        else
            Console.WriteLine($"[INFO] {aclMsj}");

        // ---- 5. Migrar archivos legacy si existen (no borrar, solo respaldar) ----
        MigrarBdcLegada(raiz);

        // ---- 6. Copiar ejecutables (exe + DLLs) directo a la raiz ----
        CopiarDirectorio(carpetaFuenteSync, raiz); // Excluye wwwroot y appsettings por patrones

        // ---- 7. Generar appsettings.json en la raiz con credenciales verificadas y ACLs restrictivas ----
        var plantilla = Path.Combine(carpetaFuenteSync, "appsettings.json");
        if (!File.Exists(plantilla))
            throw new InvalidOperationException(
                "No se encontro la plantilla appsettings.json en: " + plantilla);

        var rutaAppSettings = Path.Combine(raiz, "appsettings.json");
        EscribirAppSettings(plantilla, rutaAppSettings, rutaFdb, usuario, password,
            mobileRemoteAccess, hostnameSaneado);

        // ---- 7b. Generar appsettings.Local.json con la contraseña del panel web ----
        var rutaAppSettingsLocal = Path.Combine(raiz, "appsettings.Local.json");
        EscribirAppSettingsLocal(rutaAppSettingsLocal, webPassword);

        // ---- 8. Retornar informacion de instalacion (el marcador se escribe en el wizard) ----
        var info = new InstalacionInfo
        {
            Raiz = raiz,
            ExeSync = exeDestino,
            ExeAdmin = Path.Combine(AppContext.BaseDirectory, "Rutx.Sincronizador.Admin.exe"),
            Fecha = DateTime.Now,
            BdPath = Path.GetFullPath(rutaFdb),
            UsuarioFb = usuario
        };

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
    public static (bool aclsOk, string mensaje) CrearEstructuraMicrosipExtras()
    {
        Directory.CreateDirectory(InstalacionPaths.MicrosipExtrasRaiz);
        Directory.CreateDirectory(InstalacionPaths.RutaFotos);
        Directory.CreateDirectory(InstalacionPaths.RutaStaging);
        Directory.CreateDirectory(InstalacionPaths.RutaRespaldos);
        Directory.CreateDirectory(InstalacionPaths.RutaLogs);

        return EstablecerAcls(InstalacionPaths.MicrosipExtrasRaiz);
    }

    private static (bool, string) EstablecerAcls(string directorio)
    {
        if (!OperatingSystem.IsWindows()) return (true, "No es Windows");
        
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
            return (true, $"ACLs aplicadas en {directorio} (Administrators, SYSTEM, NetworkService)");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudieron aplicar las ACLs en {directorio}: {ex.Message}");
        }
    }

    /// <summary>
    /// Aplica permisos heredables a la raiz de instalacion (C:\ProgramData\RUTX\Sincronizador\).
    /// El subproceso del sync necesita Modify (lectura + escritura) en Logs\ y backups\,
    /// pero C:\ProgramData\ solo da ReadAndExecute a Users por defecto.
    /// NO rompe la herencia existente: solo agrega reglas.
    /// </summary>
    private static (bool, string) EstablecerAclsRaizInstalacion(string raiz)
    {
        if (!OperatingSystem.IsWindows()) return (true, "No es Windows");
        try
        {
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var dInfo = new DirectoryInfo(raiz);
            var security = dInfo.GetAccessControl();

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system  = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            var users   = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var currentUser = WindowsIdentity.GetCurrent().User;

            security.AddAccessRule(new FileSystemAccessRule(admins,  FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system,  FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.Modify,      inherit, PropagationFlags.None, AccessControlType.Allow));
            // El grupo Users necesita Modify para que el sync (proceso hijo) escriba Logs\ y backups\
            security.AddAccessRule(new FileSystemAccessRule(users,   FileSystemRights.Modify,      inherit, PropagationFlags.None, AccessControlType.Allow));
            // El usuario interactivo que realizó la instalación siempre tiene control total
            if (currentUser != null)
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

            dInfo.SetAccessControl(security);
            return (true, $"ACLs de instalacion aplicadas en {raiz}");
        }
        catch (Exception ex)
        {
            return (false, $"No se pudieron aplicar ACLs de instalacion en {raiz}: {ex.Message}");
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
    /// Genera appsettings.json en la raiz de instalacion con las credenciales verificadas
    /// y aplica ACLs restrictivas al archivo generado.
    /// La contraseña del panel web (WebAuth:AdminPassword) NO se escribe aqui:
    /// va exclusivamente en appsettings.Local.json.
    /// </summary>
    private static void EscribirAppSettings(string plantilla, string destino,
        string rutaFdb, string usuario, string password,
        bool mobileRemoteAccess = false, string? publicHostname = null)
    {
        var nodo = JsonNode.Parse(File.ReadAllText(plantilla))
                   ?? throw new InvalidOperationException("La plantilla appsettings.json no es JSON valido.");

        ConstruirAppSettingsJson(nodo.AsObject(), rutaFdb, usuario, password, mobileRemoteAccess, publicHostname);

        File.WriteAllText(destino, nodo.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AplicarAclAppSettings(destino);
    }

    /// <summary>
    /// Muta un JsonObject de plantilla aplicando la configuracion de la
    /// instalacion. Es puro (no escribe archivos ni aplica ACLs) para poder
    /// probarse de forma unitaria con directorios temporales.
    /// Exposed as internal para los tests de Rutx.Sincronizador.Tests.
    /// </summary>
    internal static void ConstruirAppSettingsJson(JsonObject obj,
        string rutaFdb, string usuario, string password,
        bool mobileRemoteAccess = false, string? publicHostname = null)
    {
        if (!obj.ContainsKey("ConnectionStrings") || obj["ConnectionStrings"] is not JsonObject conexiones)
        {
            conexiones = new JsonObject();
            obj["ConnectionStrings"] = conexiones;
        }

        // Construir la cadena con la contraseña real verificada
        var cadena = FbConexionHelper.ConstruirCadena(rutaFdb, usuario, password);
        cadena = cadena.Replace("Pooling=False", "Pooling=True");

        conexiones["FirebirdConnection"] = cadena;

        // Actualizar ruta SQLite al path canonico de Microsip Extras (Consolidacion Fase B)
        if (!obj.ContainsKey("ComplementariaDb") || obj["ComplementariaDb"] is not JsonObject comple)
        {
            comple = new JsonObject();
            obj["ComplementariaDb"] = comple;
        }
        comple["Ruta"] = InstalacionPaths.RutaDb;

        // Limpiar configuraciones legadas
        obj.Remove("WebSqlite");
        obj.Remove("ColaOffline");

        if (!obj.ContainsKey("Storage") || obj["Storage"] is not JsonObject storage)
        {
            storage = new JsonObject();
            obj["Storage"] = storage;
        }
        storage["FotosPath"] = InstalacionPaths.RutaFotos;

        // Generar Jwt:Key segura aleatoria (64 bytes en Base64) para produccion
        JsonObject jwtObj;
        if (!obj.ContainsKey("Jwt") || obj["Jwt"] is not JsonObject)
        {
            jwtObj = new JsonObject();
            obj["Jwt"] = jwtObj;
        }
        else
        {
            jwtObj = (JsonObject)obj["Jwt"]!;
        }
        var keyBytes = new byte[64];
        System.Security.Cryptography.RandomNumberGenerator.Fill(keyBytes);
        jwtObj["Key"] = Convert.ToBase64String(keyBytes);

        // Generar credenciales admin para el panel web (con rotacion obligatoria al primer login)
        JsonObject webAuthObj;
        if (!obj.ContainsKey("WebAuth") || obj["WebAuth"] is not JsonObject)
        {
            webAuthObj = new JsonObject();
            obj["WebAuth"] = webAuthObj;
        }
        else
        {
            webAuthObj = (JsonObject)obj["WebAuth"]!;
        }
        webAuthObj["AdminUsername"] = "admin";
        webAuthObj.Remove("AdminPassword");

        if (mobileRemoteAccess)
        {
            if (!obj.ContainsKey("Network") || obj["Network"] is not JsonObject network)
            {
                network = new JsonObject();
                obj["Network"] = network;
            }
            network["ExternalApiEnabled"] = true;
            network["ExternalPort"] = 5048;
            network["ExternalApiMode"] = "ReverseProxy";

            var hostname = Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.Sanitizar(publicHostname);
            obj["AllowedHosts"] = $"{hostname};localhost;127.0.0.1";

            if (!obj.ContainsKey("Cloudflare") || obj["Cloudflare"] is not JsonObject cloudflare)
            {
                cloudflare = new JsonObject();
                obj["Cloudflare"] = cloudflare;
            }
            cloudflare["PublicHostname"] = hostname;
        }
        else
        {
            obj.Remove("Network");
            obj.Remove("Cloudflare");
            obj["AllowedHosts"] = "*";
        }
    }

    private static void AplicarAclAppSettings(string rutaArchivo)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(rutaArchivo);
            var security = fi.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var service = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var currentUser = WindowsIdentity.GetCurrent().User;

            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(service, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            if (currentUser != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            fi.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AVISO] No se pudieron restringir ACLs de appsettings.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Genera appsettings.Local.json con la contraseña del panel web.
    /// Este archivo es sensible y NO se incluye en Git ni en el ZIP de entrega.
    /// La contraseña se guarda SOLO aqui (nunca en appsettings.json).
    /// </summary>
    private static void EscribirAppSettingsLocal(string destino, string webPassword)
    {
        var local = new JsonObject();
        var webAuth = new JsonObject();
        webAuth["AdminPassword"] = webPassword;
        local["WebAuth"] = webAuth;

        File.WriteAllText(destino, local.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AplicarAclAppSettings(destino);
    }

    /// <summary>
    /// Limpia un hostname eliminando protocolo (https://, http://), puertos,
    /// rutas y barras finales. Devuelve solo el dominio (ej: "sync.cliente.com").
    /// Si el valor es vacío o nulo, devuelve "" (ya NO devuelve un placeholder).
    /// Delega en CloudflareHostnamePolicy para mantener una unica fuente de verdad.
    /// </summary>
    public static string SanitizarHostname(string? raw)
        => Rutx.Sincronizador.Shared.CloudflareHostnamePolicy.Sanitizar(raw);

    /// <summary>
    /// Crea un acceso directo (.lnk) en el Escritorio del usuario apuntando al exe indicado.
    /// Usa WScript.Shell via COM interop (disponible en todas las versiones de Windows).
    /// </summary>
    public static void CrearAccesoDirectoEscritorio(string rutaExe, string nombreCorto = "RUTX Sincronizador")
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var escritorio = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var rutaLnk = Path.Combine(escritorio, nombreCorto + ".lnk");

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            var shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            object? shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { rutaLnk });

            if (shortcut == null) return;

            shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { rutaExe });
            shortcut.GetType().InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(rutaExe) ?? "" });
            shortcut.GetType().InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "Panel de administración del Sincronizador RUTX" });
            shortcut.GetType().InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch
        {
            // Si falla (COM no disponible, permisos), no es crítico.
        }
    }
}
