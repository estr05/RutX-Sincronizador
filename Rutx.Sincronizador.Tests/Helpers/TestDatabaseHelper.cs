using Microsoft.Data.Sqlite;
using Rutx.Sincronizador.Data;

namespace Rutx.Sincronizador.Tests.Helpers;

public static class TestDatabaseHelper
{
    public static ColaOfflineRepository CrearBaseDatosEnMemoria()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return new ColaOfflineRepository(connection);
    }
}
