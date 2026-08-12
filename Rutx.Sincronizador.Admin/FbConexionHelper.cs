using FirebirdSql.Data.FirebirdClient;

namespace Rutx.Sincronizador.Admin;

/// <summary>
/// Utilidades para validar la BD Firebird de Microsip durante la instalacion:
///  - que el archivo .fdb exista y tenga la firma de una BD Firebird,
///  - probar la conexion con un par usuario/contraseña (timeout corto).
/// 100% SOLO LECTURA: solo abre y cierra la conexion, nunca escribe.
/// </summary>
public static class FbConexionHelper
{
    /// <summary>
    /// True si el archivo existe y empieza con la firma de una BD Firebird.
    /// La primera pagina de un .fdb es siempre una header page (page_type=0x01).
    /// Los bytes 2-3 (checksum) pueden variar segun la configuracion de Firebird,
    /// por lo que solo se valida el page type. Compatible con todas las versiones:
    /// Firebird 1.x, 2.x, 3.0, 4.0, 5.0.
    /// </summary>
    public static bool ArchivoFdbValido(string rutaFdb, out string mensaje)
    {
        mensaje = "";
        if (string.IsNullOrWhiteSpace(rutaFdb))
        {
            mensaje = "No se indico ninguna ruta de BD.";
            return false;
        }

        if (!File.Exists(rutaFdb))
        {
            mensaje = "El archivo no existe en la ruta indicada.";
            return false;
        }

        try
        {
            using var fs = new FileStream(rutaFdb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 16)
            {
                mensaje = "El archivo es demasiado pequeno para ser una BD Firebird.";
                return false;
            }

            var cabecera = new byte[4];
            // ReadExactly: garantiza leer los 4 bytes (CA2022), lanza EOS solo si
            // el archivo termina antes (ya descartado con la validacion de Length).
            fs.ReadExactly(cabecera);

            // Firma Firebird: page_type=0x01 (header page) en el byte 0.
            // Los bytes 2-3 (checksum) pueden ser 00 00 o 39 30 segun la
            // configuracion, por lo que solo se valida el page type.
            // Compatible con todas las versiones: Firebird 1.x, 2.x, 3.0, 4.0, 5.0.
            bool firma = cabecera[0] == 0x01 && cabecera[1] == 0x00;

            if (!firma)
            {
                mensaje = "El archivo no tiene la firma de una BD Firebird (podria ser otro tipo de archivo).";
                return false;
            }

            mensaje = "Archivo .fdb valido (firma Firebird detectada).";
            return true;
        }
        catch (Exception ex)
        {
            mensaje = "No se pudo leer el archivo: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Construye la cadena de conexion Firebird con los datos elegidos.
    /// DataSource localhost / puerto 3050 (instancia local de Microsip).
    /// </summary>
    public static string ConstruirCadena(string rutaFdb, string usuario, string password)
    {
        var csb = new FbConnectionStringBuilder
        {
            Database = rutaFdb,
            UserID = string.IsNullOrWhiteSpace(usuario) ? "SYSDBA" : usuario.Trim(),
            Password = password ?? "",
            DataSource = "localhost",
            Port = 3050,
            Dialect = 3,
            Pooling = false,     // la prueba de instalacion es puntual
            Charset = "UTF8"
        };
        return csb.ConnectionString;
    }

    /// <summary>
    /// Intenta abrir la conexion con la ruta y credenciales dadas.
    /// No deja la conexion abierta: se cierra (y descarta) siempre.
    /// </summary>
    public static bool ProbarConexion(string rutaFdb, string usuario, string password,
        out string mensaje, int timeoutSegundos = 6)
    {
        mensaje = "";
        if (!ArchivoFdbValido(rutaFdb, out var msjArchivo))
        {
            mensaje = msjArchivo;
            return false;
        }

        // Pre-check rapido de red: si no hay servidor Firebird en localhost:3050,
        // no tiene sentido esperar al cliente ADO (que puede tardar o colgarse).
        using (var tcp = new System.Net.Sockets.TcpClient())
        {
            try { tcp.ConnectAsync("localhost", 3050).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* servidor inaccesible */ }
            if (!tcp.Connected)
            {
                mensaje = "No se pudo contactar al servidor Firebird (localhost:3050). Verifica que el servicio Firebird esté en ejecución.";
                return false;
            }
        }

        var cadena = ConstruirCadena(rutaFdb, usuario, password);
        // Aplicar el timeout a la conexion de prueba (por si Firebird no responde)
        cadena = cadena.Replace(";Dialect=3", $";Dialect=3;Connection Timeout={Math.Max(1, timeoutSegundos)}");
        try
        {
            using var conn = new FbConnection(cadena);
            conn.Open();

            // Verificacion minima de lectura: nombre y version del motor.
            // MON$DATABASE_VERSION existe desde Firebird 2.5; en versiones
            // anteriores (2.1) solo esta MON$DATABASE_NAME.
            using var cmd = conn.CreateCommand();
            string version;
            try
            {
                cmd.CommandText = "SELECT MON$DATABASE_VERSION FROM MON$DATABASE";
                version = cmd.ExecuteScalar()?.ToString()?.Trim() ?? "desconocida";
            }
            catch
            {
                cmd.CommandText = "SELECT MON$DATABASE_NAME FROM MON$DATABASE";
                version = cmd.ExecuteScalar()?.ToString()?.Trim() ?? "desconocida";
            }

            mensaje = $"Conexion OK (Firebird {version}).";
            return true;
        }
        catch (FbException ex)
        {
            var limpio = ex.Message.Replace(password, "****").Replace(cadena, "[cadena]");
            mensaje = ClasificarError(limpio, ex.ErrorCode);
            return false;
        }
        catch (Exception ex)
        {
            var limpio = ex.Message.Replace(password, "****").Replace(cadena, "[cadena]");
            mensaje = "Fallo la conexión: " + limpio;
            return false;
        }
    }

    /// <summary>
    /// Convierte el error crudo del cliente Firebird en un mensaje claro para
    /// el instalador: distingue credenciales incorrectas, servidor caido,
    /// BD no abrible y problemas de la base de seguridad.
    /// </summary>
    private static string ClasificarError(string limpio, int errorCode)
    {
        var m = limpio;

        // 1) Credenciales incorrectas (codigo 335544472 o frase distintiva)
        if (errorCode == 335544472 ||
            m.Contains("user name and password", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("password are not defined", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("usuario y contras", StringComparison.OrdinalIgnoreCase))
            return "Credenciales incorrectas: el usuario o la contraseña no son válidos para esta base de datos.";

        // 2) Servidor Firebird inalcanzable / caido (codigos 335544344/335544352)
        if (errorCode == 335544344 || errorCode == 335544352 ||
            m.Contains("unable to connect to remote host", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("failed to connect to server", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("network request to host", StringComparison.OrdinalIgnoreCase))
            return "No se pudo contactar al servidor Firebird (localhost:3050). Verifica que el servicio Firebird esté en ejecución.";

        // 3) BD no abrible (ruta o permisos)
        if (m.Contains("unable to open database", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("database not found", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("file not found", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("no se pudo abrir", StringComparison.OrdinalIgnoreCase))
            return "La base de datos no se pudo abrir: verifica la ruta del archivo .fdb y sus permisos.";

        // 4) Base de seguridad
        if (m.Contains("password database", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("cannot attach", StringComparison.OrdinalIgnoreCase))
            return "Firebird rechazó la conexión (base de seguridad). Verifica el usuario con el administrador.";

        // 5) Generico
        return "Fallo la conexión: " + limpio;
    }
}
