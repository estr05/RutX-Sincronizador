using Microsoft.Data.Sqlite;
using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Data.Sqlite;

namespace Rutx.Sincronizador.Tests.Helpers;

public class MockSqliteConnectionFactory : ISqliteConnectionFactory
{
    public string ConnectionString { get; }
    private SqliteConnection? _sharedConnection;

    public MockSqliteConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_sharedConnection == null)
        {
            _sharedConnection = new SqliteConnection(ConnectionString);
            _sharedConnection.Open();
        }
        
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return Task.FromResult(conn);
    }
}

public static class TestDatabaseHelper
{
    public static ColaOfflineRepository CrearBaseDatosEnMemoria()
    {
        string connectionString = $"Data Source={System.Guid.NewGuid()};Mode=Memory;Cache=Shared";
        var factory = new MockSqliteConnectionFactory(connectionString);
        var repo = new ColaOfflineRepository(factory);

        using var conn = factory.CreateConnectionAsync().Result;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS rutx_cola_operaciones (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                operacion_id        TEXT    NOT NULL UNIQUE,
                tipo_operacion      TEXT    NOT NULL,
                payload             TEXT    NOT NULL,
                estado              TEXT    NOT NULL DEFAULT 'PENDIENTE',
                intentos            INTEGER NOT NULL DEFAULT 0,
                max_intentos        INTEGER NOT NULL DEFAULT 5,
                siguiente_reintento TEXT    NOT NULL,
                error_ultimo_intento TEXT   NULL,
                fecha_creacion      TEXT    NOT NULL,
                fecha_modificacion  TEXT    NOT NULL,
                lease_until         TEXT    NULL,
                dead_letter         INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_estado ON rutx_cola_operaciones(estado);
            CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_lease  ON rutx_cola_operaciones(lease_until);
            CREATE INDEX IF NOT EXISTS ix_rutx_cola_oper_reintento ON rutx_cola_operaciones(siguiente_reintento);
        ";
        cmd.ExecuteNonQuery();

        return repo;
    }
}
