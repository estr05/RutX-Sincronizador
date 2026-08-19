namespace Rutx.Sincronizador.Data.Sqlite;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface ISqliteConnectionFactory
{
    string ConnectionString { get; }
    Task<SqliteConnection> CreateConnectionAsync(CancellationToken cancellationToken = default);
}
