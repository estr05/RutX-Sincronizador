using System.Text.RegularExpressions;
using FirebirdSql.Data.FirebirdClient;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Categorias de error SQL/Firebird que el sincronizador puede encontrar al
/// escribir en la base de datos de Microsip. Cada categoria se traduce a un
/// mensaje accionable (en espanol) y a un estado HTTP apropiado.
/// </summary>
public enum SqlErrorTipo
{
    /// <summary>No se pudo clasificar (o no es un error SQL).</summary>
    Desconocido,

    /// <summary>violation of FOREIGN KEY constraint: un ID referenciado no existe.</summary>
    ViolacionLlaveForanea,

    /// <summary>NULL en una columna NOT NULL (cannot insert NULL into ...).</summary>
    ViolacionNotNull,

    /// <summary>Valor fuera del rango permitido por una constraint CHECK.</summary>
    ViolacionCheck,

    /// <summary>Llave primaria/unicidad duplicada (attempt to store duplicate value).</summary>
    ValorDuplicado,

    /// <summary>Tabla o columna inexistente (Table unknown / Column unknown).</summary>
    TablaOColumnaInexistente,

    /// <summary>No se pudo conectar al servidor Firebird.</summary>
    ErrorConexion,

    /// <summary>La BD fue creada con un Firebird mas nuevo que el servidor local (ODS).</summary>
    OdsNoSoportada,

    /// <summary>Deadlock / lock conflict / update conflicts: operacion reintentable.</summary>
    BloqueoODeadlock,

    /// <summary>Error de sintaxis SQL o diferencia de esquema entre versiones.</summary>
    ErrorSintaxisSql,

    /// <summary>El usuario no tiene permiso sobre el objeto (no permission for ...).</summary>
    PermisosInsuficientes,

    /// <summary>Intento de escritura en una base de datos de solo lectura.</summary>
    BaseDatosSoloLectura,
}

/// <summary>Resultado del clasificador: tipo, detalle y mensaje accionable.</summary>
public sealed class SqlErrorInfo
{
    public SqlErrorTipo Tipo { get; set; }
    public bool EsReintentable { get; set; }
    public string MensajeAmigable { get; set; } = string.Empty;
    public string Detalle { get; set; } = string.Empty;
    public string? Constraint { get; set; }
    public string? Tabla { get; set; }
    public string? Columna { get; set; }
    public int? GdsCode { get; set; }
    public int? SqlCode { get; set; }
}

/// <summary>
/// Clasifica excepciones de Firebird (o cualquier excepcion cuyo mensaje, o el
/// de sus inner exceptions, sea un mensaje de error de Firebird) en categorias
/// accionables. Soporta Firebird 2.5, 3.0, 4.0 y 5.0: los mensajes de error del
/// motor son estables en ingles a traves de todas las versiones, por eso el
/// matching principal es por texto y los GDS/SQL codes son un refuerzo.
/// </summary>
public static class SqlErrorClassifier
{
    private static readonly Regex RxConstraint =
        new("constraint \"([^\"]+)\" on table \"([^\"]+)\"", RegexOptions.IgnoreCase);

    private static readonly Regex RxConstraintSolo =
        new("constraint \"([^\"]+)\"", RegexOptions.IgnoreCase);

    private static readonly Regex RxColumnaNotNull =
        new("cannot insert NULL into \\(([^)]+)\\)", RegexOptions.IgnoreCase);

    private static readonly Regex RxTablaDesconocida =
        new("table unknown \"?([A-Z0-9_$]+)\"?", RegexOptions.IgnoreCase);

    private static readonly Regex RxColumnaDesconocida =
        new("column unknown \"?([A-Z0-9_$]+)\"?", RegexOptions.IgnoreCase);

    private static readonly Regex RxSqlCode =
        new(@"SQL error code\s*=\s*(-?\d+)", RegexOptions.IgnoreCase);

    // GDS codes de Firebird usados como refuerzo cuando el texto no es concluyente
    // (estables en 2.5-5.0). Ver FirebirdSql.Data.FirebirdClient.FbError.Number.
    private const int GdsWrongOds = 335544947;          // isc_wrong_ods
    private const int GdsNetworkError = 335544721;      // isc_network_error
    private const int GdsDeadlock = 335544336;          // isc_deadlock
    private const int GdsLockConflict = 335544345;      // isc_lock_conflict
    private const int GdsUpdateConflict = 335544365;    // isc_update_conflict
    private const int GdsForeignKey = 335544449;        // isc_foreign_key
    private const int GdsNotNull = 335544467;           // isc_not_null
    private const int GdsUniqueKey = 803;               // unique_key_violation

    public static SqlErrorInfo Clasificar(Exception ex)
    {
        var mensajes = new List<string>();
        var gdsCodes = new List<int>();

        for (var actual = ex; actual != null; actual = actual.InnerException)
        {
            if (!string.IsNullOrEmpty(actual.Message))
                mensajes.Add(actual.Message);

            if (actual is FbException fb)
            {
                if (fb.ErrorCode != 0)
                    gdsCodes.Add(fb.ErrorCode);
                if (fb.Errors != null)
                    gdsCodes.AddRange(fb.Errors.Select(e => e.Number).Where(n => n != 0));
            }
        }

        var texto = string.Join("\n", mensajes);
        var info = new SqlErrorInfo { Detalle = texto, GdsCode = gdsCodes.FirstOrDefault() == 0 ? null : gdsCodes.FirstOrDefault() };

        var sqlCode = RxSqlCode.Match(texto) is { Success: true } matchSqlCode ? int.Parse(matchSqlCode.Groups[1].Value) : (int?)null;
        info.SqlCode = sqlCode;

        // ---- 1. ODS no soportada (version de BD mas nueva que el servidor) ----
        if (gdsCodes.Contains(GdsWrongOds) || Contiene(texto, "unsupported on-disk structure", "wrong on-disk structure", "wrong ODS"))
        {
            return Fijar(info, SqlErrorTipo.OdsNoSoportada,
                "La base de datos fue creada con una version de Firebird mas reciente que la del servidor " +
                "que esta ejecutando el sincronizador. Instala el servidor Firebird adecuado (el aviso indica " +
                "la version detectada y la soportada) o verifica la cadena de conexion.",
                reintentable: false);
        }

        // ---- 2. Conexion ----
        if (gdsCodes.Contains(GdsNetworkError) || Contiene(texto,
                "connection refused", "failed to connect", "no connection could be made",
                "unable to complete network request", "database server unavailable",
                "unavailable database", "host not found", "network is unreachable",
                "connection attempt failed", "the network path was not found",
                "provider did not return a connection", "tcp error"))
        {
            return Fijar(info, SqlErrorTipo.ErrorConexion,
                "No se pudo conectar con la base de datos Firebird. Verifica que el servidor Firebird este " +
                "encendido y accesible, y que la cadena de conexion (servidor, puerto y ruta de la BD) sea correcta.",
                reintentable: true);
        }

        // ---- 3. Bloqueos / deadlock (reintentables) ----
        if (gdsCodes.Contains(GdsDeadlock) || gdsCodes.Contains(GdsLockConflict) || gdsCodes.Contains(GdsUpdateConflict)
            || Contiene(texto, "deadlock", "lock conflict on no wait transaction",
                "update conflicts with concurrent update", "no lock available",
                "could not adjust the lock"))
        {
            return Fijar(info, SqlErrorTipo.BloqueoODeadlock,
                "La base de datos esta ocupada (bloqueo o deadlock con otra operacion de Microsip o del " +
                "sincronizador). La operacion se reintentara automaticamente.",
                reintentable: true);
        }

        // ---- 4. FK ----
        if (gdsCodes.Contains(GdsForeignKey) || Contiene(texto, "violation of FOREIGN KEY constraint"))
        {
            var m = RxConstraint.Match(texto);
            var c = m.Success ? m.Groups[1].Value : RxConstraintSolo.Match(texto).Groups[1].Value;
            var t = m.Success ? m.Groups[2].Value : null;
            info.Constraint = string.IsNullOrEmpty(c) ? null : c;
            info.Tabla = t;
            return Fijar(info, SqlErrorTipo.ViolacionLlaveForanea,
                $"Violacion de llave foranea{(c.Length > 0 ? $" (constraint \"{c}\")" : string.Empty)}" +
                $"{(t is not null ? $" en la tabla \"{t}\"" : string.Empty)}: el registro referenciado no existe o " +
                "fue eliminado. Revisa los IDs configurados en el panel (seccion IDs Microsip) y que los datos " +
                "del cliente esten completos.",
                reintentable: false);
        }

        // ---- 5. NOT NULL ----
        // Nota: "validation error for column" NO es NOT NULL: es una violacion
        // de CHECK/dominio (valor invalido), por eso vive en el bloque CHECK.
        if (gdsCodes.Contains(GdsNotNull) || Contiene(texto, "cannot insert NULL into", "NULL assignment to NOT NULL column"))
        {
            var col = RxColumnaNotNull.Match(texto).Groups[1].Value;
            info.Columna = string.IsNullOrEmpty(col) ? null : col.Trim();
            return Fijar(info, SqlErrorTipo.ViolacionNotNull,
                $"La operacion intenta guardar un valor vacio en el campo \"{info.Columna ?? "?"}\" que la base " +
                "de datos no permite (NOT NULL). Revisa que la informacion enviada desde la app este completa.",
                reintentable: false);
        }

        // ---- 6. CHECK ----
        // "validation error for column X, value Y cannot be validated" es el
        // mensaje de una violacion de CHECK/dominio (valor fuera de la regla),
        // no de una columna vacia. Ya no puede llegar aqui con GdsNotNull
        // porque el bloque 5 lo habria capturado antes.
        if (Contiene(texto,
                "violation of CHECK constraint", "invalid field value in CHECK constraint",
                "validation error for column"))
        {
            var m = RxConstraint.Match(texto);
            var c = m.Success ? m.Groups[1].Value : string.Empty;
            info.Constraint = string.IsNullOrEmpty(c) ? null : c;

            // Sub-caso: "value *** null" es un NULL rechazado por un dominio
            // con CHECK (NOT NULL via dominio). El mensaje debe hablar de valor
            // vacio, no de "tipo de documento", para no confundir al usuario.
            var esNullViaDominio = Contiene(texto, "value *** null");
            return Fijar(info, SqlErrorTipo.ViolacionCheck,
                esNullViaDominio
                    ? $"La operacion intenta guardar un valor vacio en un campo que el dominio de la " +
                      $"base de datos no admite (constraint \"{info.Constraint ?? "?"}\"). Revisa que la " +
                      $"informacion enviada desde la app este completa."
                    : $"El valor enviado no cumple una regla de la base de datos (constraint " +
                      $"\"{info.Constraint ?? "?"}\"). Verifica el tipo de documento, estatus y demas valores del registro.",
                reintentable: false);
        }

        // ---- 7. Duplicados ----
        if (gdsCodes.Contains(GdsUniqueKey) || Contiene(texto, "attempt to store duplicate value", "duplicate value in unique index"))
        {
            return Fijar(info, SqlErrorTipo.ValorDuplicado,
                "Ya existe un registro con la misma llave o folio (otro proceso pudo haberlo creado). " +
                "El sincronizador detectara la situacion y conciliara en el siguiente intento.",
                reintentable: false);
        }

        // ---- 8. Tabla / columna inexistente ----
        if (Contiene(texto, "table unknown", "is not a known table"))
        {
            info.Tabla = RxTablaDesconocida.Match(texto).Groups[1].Value;
            return Fijar(info, SqlErrorTipo.TablaOColumnaInexistente,
                $"La tabla \"{info.Tabla ?? "?"}\" no existe en la base de datos. Verifica que la base sea una " +
                "base de datos de Microsip compatible (version reciente) y que la cadena de conexion apunte a " +
                "la base correcta.",
                reintentable: false);
        }

        if (Contiene(texto, "column unknown", "invalid column reference"))
        {
            info.Columna = RxColumnaDesconocida.Match(texto).Groups[1].Value;
            return Fijar(info, SqlErrorTipo.TablaOColumnaInexistente,
                $"La columna \"{info.Columna ?? "?"}\" no existe en la base de datos. Esto puede indicar una " +
                "diferencia de esquema entre versiones de Microsip. Revisa la tabla involucrada en el detalle " +
                "del error.",
                reintentable: false);
        }

        // ---- 9. Permisos / solo lectura ----
        if (Contiene(texto, "attempted update of read-only database", "database is read-only"))
        {
            return Fijar(info, SqlErrorTipo.BaseDatosSoloLectura,
                "La base de datos esta en modo de solo lectura. Verifica los permisos del archivo .fdb y que " +
                "el servidor Firebird la haya abierto para escritura.",
                reintentable: false);
        }

        if (Contiene(texto, "no permission for", "is not defined for user", "no select access",
                "no insert access", "no update access", "no delete access", "insufficient privileges",
                "no grant"))
        {
            return Fijar(info, SqlErrorTipo.PermisosInsuficientes,
                "El usuario de la conexion no tiene permisos sobre algun objeto de la base. Usa el usuario " +
                "SYSDBA (o uno con permisos de lectura/escritura) en la cadena de conexion.",
                reintentable: false);
        }

        // ---- 10. Sintaxis / DSQL ----
        if (Contiene(texto, "Dynamic SQL Error", "token unknown", "is not a valid token"))
        {
            return Fijar(info, SqlErrorTipo.ErrorSintaxisSql,
                "Ocurrio un error de SQL (posible diferencia de esquema entre versiones de Microsip). " +
                "Revisa el detalle tecnico y reportalo.",
                reintentable: false);
        }

        info.Tipo = SqlErrorTipo.Desconocido;
        return info;
    }

    private static SqlErrorInfo Fijar(SqlErrorInfo info, SqlErrorTipo tipo, string mensaje, bool reintentable)
    {
        info.Tipo = tipo;
        info.MensajeAmigable = mensaje;
        info.EsReintentable = reintentable;
        return info;
    }

    private static bool Contiene(string texto, params string[] fragmentos)
        => fragmentos.Any(f => texto.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
}
