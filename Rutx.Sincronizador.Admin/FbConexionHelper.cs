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
    /// La firma clasica de Firebird 2.x/3.x es "OTY2" en los primeros 4 bytes
    /// (o "OTY3" en versiones muy viejas); si la cabecera no es reconocible
    /// se reporta como BD invalida.
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

            // Firma Firebird: bytes 0x4F 0x54 0x59 0x36 ("OTY6") o 0x4F 0x54 0x59 0x33 ("OTY3")
            bool firma = (cabecera[0] == 0x4F && cabecera[1] == 0x54 && cabecera[2] == 0x59 &&
                          (cabecera[3] == 0x36 || cabecera[3] == 0x33));

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

        var cadena = ConstruirCadena(rutaFdb, usuario, password);
        // Aplicar el timeout a la conexion de prueba (por si Firebird no responde)
        cadena = cadena.Replace(";Dialect=3", $";Dialect=3;Connection Timeout={Math.Max(1, timeoutSegundos)}");
        try
        {
            using var conn = new FbConnection(cadena);
            conn.Open();

            // Verificacion minima de lectura: version del motor (SELECT a MON$DATABASE)
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT MON$DATABASE_VERSION FROM MON$DATABASE";
            var version = cmd.ExecuteScalar()?.ToString()?.Trim() ?? "desconocida";

            mensaje = $"Conexion OK (Firebird {version}).";
            return true;
        }
        catch (Exception ex)
        {
            var limpio = ex.Message.Replace(password, "****").Replace(cadena, "[cadena]");
            mensaje = "Fallo la conexion: " + limpio;
            return false;
        }
    }
}
