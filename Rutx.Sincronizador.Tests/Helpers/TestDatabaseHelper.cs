using Microsoft.Data.Sqlite;
using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Data.Sqlite;

namespace Rutx.Sincronizador.Tests.Helpers;

public class MockSqliteConnectionFactory : ISqliteConnectionFactory
{
    public string ConnectionString { get; }
    public MockSqliteConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
    }
    public Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        return Task.FromResult(conn);
    }
}

public static class TestDatabaseHelper
{
    public static ColaOfflineRepository CrearBaseDatosEnMemoria()
    {
        var factory = new MockSqliteConnectionFactory("Data Source=:memory:");
        var repo = new ColaOfflineRepository(factory);
        // Ensure schema is created since we aren't opening it manually here
        using var conn = factory.CreateConnectionAsync().Result;
        return repo;
    }
}
