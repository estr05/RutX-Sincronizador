using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Data.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del aplicador de migraciones versionadas de la BD web (Bloque 0):
///  - Esquema nuevo: aplica v001, crea schema_version y todas las tablas/índices.
///  - Idempotencia: aplicar dos veces no duplica versiones ni rompe nada.
///  - Evolución: una BD "vieja" (schema_version sin tablas) migra sin pérdida.
///  - Respaldo: antes de la primera migración pendiente se genera un .db en backups/.
/// </summary>
public class WebSqliteMigratorTests
{
    private static string BaseTemp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rutx-web-migrator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Conectar(string dbPath) => $"Data Source={dbPath}";

    [Fact]
    public async Task AplicaV001_CreaSchemaVersionYTablas()
    {
        var dir = BaseTemp();
        var db = Path.Combine(dir, "web.db");
        var migrator = new WebSqliteMigrator(Conectar(db), logger: NullLogger<WebSqliteMigrator>.Instance);

        var version = await migrator.ApplyAsync();

        Assert.Equal(6, version);
        Assert.Equal(6, await migrator.VersionVigenteAsync());

        await using var conn = new SqliteConnection(Conectar(db));
        await conn.OpenAsync();

        var tablas = await TablasAsync(conn);
        Assert.Contains("schema_version", tablas);
        Assert.Contains("web_users", tablas);
        Assert.Contains("web_zones", tablas);
        Assert.Contains("web_zone_sellers", tablas);
        Assert.Contains("web_notifications", tablas);
        Assert.Contains("web_audit_log", tablas);

        var indices = await IndicesAsync(conn);
        Assert.Contains("ix_web_users_username", indices);
        Assert.Contains("ix_web_users_zone_ids", indices);
        Assert.Contains("ix_web_notifications_target", indices);
        Assert.Contains("ix_web_notifications_status", indices);
        Assert.Contains("ix_web_audit_log_created", indices);
        Assert.Contains("ix_web_audit_log_username", indices);

        // v002: zonas autorizadas por usuario (JSON; vacío = sin restricción).
        await using var columna = conn.CreateCommand();
        columna.CommandText = "SELECT COUNT(*) FROM pragma_table_info('web_users') WHERE name = 'zone_ids';";
        Assert.Equal(1L, await columna.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AplicarDosVeces_EsIdempotente()
    {
        var dir = BaseTemp();
        var db = Path.Combine(dir, "web.db");
        var migrator = new WebSqliteMigrator(Conectar(db), logger: NullLogger<WebSqliteMigrator>.Instance);

        await migrator.ApplyAsync();
        await migrator.ApplyAsync();

        await using var conn = new SqliteConnection(Conectar(db));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Assert.Equal(6L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BdBidireccionalExistente_MigraSinPerderDatos()
    {
        var dir = BaseTemp();
        var db = Path.Combine(dir, "web.db");

        // BD "vieja" con datos propios y tabla de control en versión 0 (sin esquema web).
        await using (var conn = new SqliteConnection(Conectar(db)))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);
                CREATE TABLE datos_propios (id INTEGER PRIMARY KEY, valor TEXT NOT NULL);
                INSERT INTO datos_propios (valor) VALUES ('conservar');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var migrator = new WebSqliteMigrator(Conectar(db), logger: NullLogger<WebSqliteMigrator>.Instance);
        var version = await migrator.ApplyAsync();
        Assert.Equal(6, version);

        await using var conn2 = new SqliteConnection(Conectar(db));
        await conn2.OpenAsync();
        await using var cmd2 = conn2.CreateCommand();
        cmd2.CommandText = "SELECT valor FROM datos_propios WHERE id = 1;";
        Assert.Equal("conservar", (string)(await cmd2.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AntesDeMigrar_GeneraRespaldoConsistente()
    {
        var dir = BaseTemp();
        var db = Path.Combine(dir, "web.db");
        var migrator = new WebSqliteMigrator(Conectar(db), backupDirectory: Path.Combine(dir, "backups"), logger: NullLogger<WebSqliteMigrator>.Instance);

        await migrator.ApplyAsync();

        var respaldos = Directory.GetFiles(Path.Combine(dir, "backups"), "web-*.db");
        Assert.Single(respaldos);

        // El respaldo es una BD SQLite válida con el esquema previo a la migración
        // (instantánea VACUUM INTO: tabla de control creada, migraciones aún no aplicadas).
        await using var conn = new SqliteConnection(Conectar(respaldos[0]));
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_version';";
        Assert.Equal(1L, await cmd.ExecuteScalarAsync());

        // Aplicar de nuevo NO genera otro respaldo (no hay pendientes).
        await migrator.ApplyAsync();
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "backups"), "web-*.db"));
    }

    private static async Task<HashSet<string>> TablasAsync(SqliteConnection conn)
    {
        var tablas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tablas.Add(reader.GetString(0));
        return tablas;
    }

    private static async Task<HashSet<string>> IndicesAsync(SqliteConnection conn)
    {
        var indices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            indices.Add(reader.GetString(0));
        return indices;
    }
}
