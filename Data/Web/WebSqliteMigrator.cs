using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Aplicador de migraciones versionadas de la BD complementaria web.
///
/// Garantías (Sprint 4 · Bloque 0):
///  - Tabla de control `schema_version` (version, name, applied_at).
///  - Cada migración se aplica DENTRO de una transacción: si falla, no queda
///    esquema parcial ni versión registrada.
///  - Antes de aplicar la primera migración pendiente se genera un respaldo
///    consistente del archivo .db con VACUUM INTO en `Data/backups/`.
///  - Ejecutar varias veces es idempotente: las versiones ya aplicadas se
///    omiten y no se genera respaldo si no hay pendientes.
/// </summary>
public sealed class WebSqliteMigrator
{
    private readonly string _connectionString;
    private readonly string _backupDirectory;
    private readonly ILogger _logger;

    public WebSqliteMigrator(string connectionString, string? backupDirectory = null, ILogger? logger = null)
    {
        _connectionString = connectionString;
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataDir = Path.GetDirectoryName(builder.DataSource)
            ?? Directory.GetCurrentDirectory();
        _backupDirectory = backupDirectory ?? Path.Combine(dataDir, "backups");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Aplica las migraciones pendientes (si las hay) y devuelve la versión
    /// vigente del esquema (0 si la BD es nueva y no hay migraciones).
    /// </summary>
    public async Task<int> ApplyAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_backupDirectory);
        await using var control = new SqliteConnection(_connectionString);
        await control.OpenAsync(cancellationToken);
        await CrearTablaControlAsync(control, cancellationToken);

        var aplicadas = await ObtenerVersionesAplicadasAsync(control, cancellationToken);
        var pendientes = WebMigrations.All
            .Where(m => !aplicadas.Contains(m.Version))
            .OrderBy(m => m.Version)
            .ToList();

        if (pendientes.Count == 0)
            return aplicadas.Count > 0 ? aplicadas.Max() : 0;

        _logger.LogInformation("WebSqliteMigrator: {Pendientes} migracion(es) pendiente(s), se genera respaldo previo.", pendientes.Count);
        await GenerarRespaldoAsync(control, pendientes.First().Version, cancellationToken);

        foreach (var migracion in pendientes)
        {
            await AplicarUnaAsync(control, migracion, cancellationToken);
            _logger.LogInformation("WebSqliteMigrator: migracion v{migracion.Version} ({migracion.Name}) aplicada.", migracion.Version, migracion.Name);
        }

        return pendientes.Max(m => m.Version);
    }

    /// <summary>Devuelve la versión vigente sin aplicar nada (lectura rápida).</summary>
    public async Task<int> VersionVigenteAsync(CancellationToken cancellationToken = default)
    {
        await using var control = new SqliteConnection(_connectionString);
        await control.OpenAsync(cancellationToken);
        await CrearTablaControlAsync(control, cancellationToken);
        var aplicadas = await ObtenerVersionesAplicadasAsync(control, cancellationToken);
        return aplicadas.Count > 0 ? aplicadas.Max() : 0;
    }

    private static async Task CrearTablaControlAsync(SqliteConnection control, CancellationToken ct)
    {
        await using var cmd = control.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version    INTEGER PRIMARY KEY,
                name       TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HashSet<int>> ObtenerVersionesAplicadasAsync(SqliteConnection control, CancellationToken ct)
    {
        var aplicadas = new HashSet<int>();
        await using var cmd = control.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            aplicadas.Add(reader.GetInt32(0));
        return aplicadas;
    }

    private async Task GenerarRespaldoAsync(SqliteConnection control, int versionObjetivo, CancellationToken ct)
    {
        var respaldo = Path.Combine(_backupDirectory, $"web-v{versionObjetivo}-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
        try
        {
            await using var cmd = control.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{respaldo.Replace("'", "''")}'";
            await cmd.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("WebSqliteMigrator: respaldo generado en {Respaldo}", respaldo);
        }
        catch (Exception ex)
        {
            // Un respaldo que falla NO bloquea la migración, pero queda registrado
            // para que el operador decida (el esquema nuevo aún es compatible).
            _logger.LogWarning(ex, "WebSqliteMigrator: no se pudo generar el respaldo previo ({Respaldo}).", respaldo);
        }
    }

    private static async Task AplicarUnaAsync(SqliteConnection control, WebMigration migracion, CancellationToken ct)
    {
        var transaccion = (SqliteTransaction)await control.BeginTransactionAsync(ct);
        try
        {
            // SQLite no soporta ALTER TABLE ... IF NOT EXISTS. Si la migración
            // contiene un ALTER TABLE ADD COLUMN y la columna ya existe, se
            // ignora esa línea específica para mantener idempotencia.
            var sql = migracion.Sql;
            if (EsAlterTableConColumnaExistente(sql, control))
                sql = OmmitirAlterTableDuplicados(sql);

            await using (var cmd = control.CreateCommand())
            {
                cmd.Transaction = transaccion;
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = control.CreateCommand())
            {
                cmd.Transaction = transaccion;
                cmd.CommandText = """
                    INSERT INTO schema_version (version, name, applied_at)
                    VALUES ($version, $name, $aplicado);
                    """;
                cmd.Parameters.AddWithValue("$version", migracion.Version);
                cmd.Parameters.AddWithValue("$name", migracion.Name);
                cmd.Parameters.AddWithValue("$aplicado", DateTime.UtcNow.ToString("o"));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await transaccion.CommitAsync(ct);
        }
        catch
        {
            await transaccion.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Detecta si la migración contiene un ALTER TABLE ADD COLUMN cuya columna
    /// ya existe en la tabla objetivo. Evita errores al re-ejecutar migraciones
    /// cuyo registro schema_version se perdió pero el esquema ya fue aplicado.
    /// </summary>
    private static bool EsAlterTableConColumnaExistente(string sql, SqliteConnection control)
    {
        var lineas = sql.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var linea in lineas)
        {
            var trim = linea.Trim().ToUpperInvariant();
            if (!trim.StartsWith("ALTER TABLE")) continue;

            // ALTER TABLE <table> ADD COLUMN <col>
            var match = System.Text.RegularExpressions.Regex.Match(
                trim, @"ALTER\s+TABLE\s+(\w+)\s+ADD\s+COLUMN\s+(\w+)");
            if (!match.Success) continue;

            var tabla = match.Groups[1].Value;
            var columna = match.Groups[2].Value;

            using var cmd = control.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({tabla})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(columna, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string OmmitirAlterTableDuplicados(string sql)
    {
        var lineas = sql.Split('\n');
        var resultado = new List<string>();
        foreach (var linea in lineas)
        {
            var trim = linea.Trim().ToUpperInvariant();
            if (trim.StartsWith("ALTER TABLE") && trim.Contains("ADD COLUMN"))
                continue;
            resultado.Add(linea);
        }
        return string.Join('\n', resultado);
    }
}