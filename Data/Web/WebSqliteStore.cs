using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Security;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Implementación del almacén web sobre SQLite (BD complementaria, contrato
/// v2 §3.1). El esquema se garantiza con WebSqliteMigrator (migraciones
/// versionadas); aquí solo hay consultas y comandos parametrizados.
/// </summary>
public sealed class WebSqliteStore : IWebSqliteStore
{
    private readonly string _connectionString;
    private readonly ILogger<WebSqliteStore> _logger;

    public WebSqliteStore(string connectionString, ILogger<WebSqliteStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<int> EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var migrator = new WebSqliteMigrator(_connectionString, logger: _logger);
        return await migrator.ApplyAsync(cancellationToken);
    }

    public async Task<int> SchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        var migrator = new WebSqliteMigrator(_connectionString, logger: _logger);
        return await migrator.VersionVigenteAsync(cancellationToken);
    }

    public async Task<WebUserRow?> FindUserByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, display_name, password_hash, roles,
                   must_change_password, password_changed_at, is_active, created_at, updated_at
            FROM web_users
            WHERE username = $username COLLATE NOCASE
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$username", username);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? LeerUsuario(reader) : null;
    }

    public async Task<WebUserRow?> FindUserByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, display_name, password_hash, roles,
                   must_change_password, password_changed_at, is_active, created_at, updated_at
            FROM web_users
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? LeerUsuario(reader) : null;
    }

    public async Task<WebUserRow> CreateUserAsync(
        string username,
        string displayName,
        string passwordHash,
        string rolesJson,
        bool mustChangePassword,
        CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_users (username, display_name, password_hash, roles,
                                   must_change_password, created_at, updated_at)
            VALUES ($username, $displayName, $passwordHash, $roles, $mustChange, $ahora, $ahora);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$displayName", displayName);
        cmd.Parameters.AddWithValue("$passwordHash", passwordHash);
        cmd.Parameters.AddWithValue("$roles", rolesJson);
        cmd.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        cmd.Parameters.AddWithValue("$ahora", ahora);
        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return new WebUserRow(id, username, displayName, passwordHash, rolesJson, mustChangePassword, null, true, ahora, ahora);
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM web_users;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public async Task<List<int>> SellersForZoneAsync(int zoneId, CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT seller_id FROM web_zone_sellers WHERE zone_id = $zoneId ORDER BY seller_id;";
        cmd.Parameters.AddWithValue("$zoneId", zoneId);
        var resultado = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            resultado.Add(reader.GetInt32(0));
        return resultado;
    }

    public async Task<List<int>> ZonesForSellerAsync(int sellerId, CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT zone_id FROM web_zone_sellers WHERE seller_id = $sellerId ORDER BY zone_id;";
        cmd.Parameters.AddWithValue("$sellerId", sellerId);
        var resultado = new List<int>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            resultado.Add(reader.GetInt32(0));
        return resultado;
    }

    public async Task<WebAuditEntry> LogAuditAsync(
        long? userId,
        string? username,
        string action,
        string? detail,
        string? ipAddress,
        string? traceId,
        CancellationToken cancellationToken = default)
    {
        var creado = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_audit_log (user_id, username, action, detail, ip_address, trace_id, created_at)
            VALUES ($userId, $username, $action, $detail, $ip, $traceId, $creado);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$userId", (object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$username", (object?)username ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ip", (object?)ipAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$traceId", (object?)traceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$creado", creado);
        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return new WebAuditEntry(id, userId, username, action, detail, ipAddress, traceId, creado);
    }

    /// <summary>
    /// Crea el usuario administrador inicial SOLO si la configuración externa
    /// (WebAuth:AdminUsername / WebAuth:AdminPassword) trae credenciales y el
    /// usuario no existe. Las credenciales NUNCA se versionan: la contraseña
    /// se lee de variable de entorno en el entorno real (política Sprint 4).
    /// El usuario nace con must_change_password=1 (rotación obligatoria en el
    /// primer acceso) y su hash se genera con WebPasswordHasher (PBKDF2).
    /// </summary>
    public async Task<bool> EnsureAdminSeedAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var username = configuration["WebAuth:AdminUsername"]?.Trim();
        var password = configuration["WebAuth:AdminPassword"];

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogWarning("WebAuth: sin credenciales de administrador inicial configuradas (WebAuth:AdminUsername/AdminPassword). No se crea ningun usuario; el login web permanece bloqueado.");
            return false;
        }

        var existente = await FindUserByUsernameAsync(username, cancellationToken);
        if (existente != null)
        {
            _logger.LogInformation("WebAuth: el usuario administrador inicial ya existe ({Username}).", username);
            return false;
        }

        var hash = WebPasswordHasher.Hash(password);
        await CreateUserAsync(
            username,
            username,
            hash,
            System.Text.Json.JsonSerializer.Serialize(new[] { WebRoleCatalog.Administrador }),
            mustChangePassword: true,
            cancellationToken);

        _logger.LogInformation("WebAuth: administrador inicial creado ({Username}) con rotacion obligatoria en el primer acceso.", username);
        return true;
    }

    private async Task<SqliteConnection> AbrirAsync(CancellationToken cancellationToken)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        return conn;
    }

    private static WebUserRow LeerUsuario(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5) != 0,
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetInt64(7) != 0,
        reader.GetString(8),
        reader.GetString(9));
}