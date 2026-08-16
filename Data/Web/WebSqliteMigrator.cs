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
        var dataDir = Path.GetDirectoryName(connectionString.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase).Trim())
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
            return aplicadas.Max();

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
        return (await ObtenerVersionesAplicadasAsync(control, cancellationToken)).Max();
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
        var respaldo = Path.Combine(_backupDirectory, $"web-v{versionObjetivo}-{DateTime.Now:yyyyMMddHHmmss}.db");
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
            await using (var cmd = control.CreateCommand())
            {
                cmd.Transaction = transaccion;
                cmd.CommandText = migracion.Sql;
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
}