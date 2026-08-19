namespace Rutx.Sincronizador.Data.Sqlite;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public class SqliteConnectionFactory : ISqliteConnectionFactory
{
    public string ConnectionString { get; }
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public SqliteConnectionFactory(string connectionString, ILogger<SqliteConnectionFactory> logger)
    {
        ConnectionString = connectionString;
        _logger = logger;
    }

    public async Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Configuración unificada de rendimiento y concurrencia
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
        ";
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
