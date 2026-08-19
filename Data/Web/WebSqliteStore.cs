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
            SELECT id, username, display_name, password_hash, roles, zone_ids,
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
            SELECT id, username, display_name, password_hash, roles, zone_ids,
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
        string zoneIdsJson = "[]",
        CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_users (username, display_name, password_hash, roles, zone_ids,
                                   must_change_password, created_at, updated_at)
            VALUES ($username, $displayName, $passwordHash, $roles, $zoneIds, $mustChange, $ahora, $ahora);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$username", username);
        cmd.Parameters.AddWithValue("$displayName", displayName);
        cmd.Parameters.AddWithValue("$passwordHash", passwordHash);
        cmd.Parameters.AddWithValue("$roles", rolesJson);
        cmd.Parameters.AddWithValue("$zoneIds", zoneIdsJson);
        cmd.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        cmd.Parameters.AddWithValue("$ahora", ahora);
        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return new WebUserRow(id, username, displayName, passwordHash, rolesJson, zoneIdsJson, mustChangePassword, null, true, ahora, ahora);
    }

    public async Task UpdateUserPasswordAsync(long userId, string passwordHash, CancellationToken cancellationToken = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE web_users
            SET password_hash = $hash, password_changed_at = $ahora,
                must_change_password = 0, updated_at = $ahora
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$hash", passwordHash);
        cmd.Parameters.AddWithValue("$ahora", ahora);
        cmd.Parameters.AddWithValue("$id", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
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
            cancellationToken: cancellationToken);

        _logger.LogInformation("WebAuth: administrador inicial creado ({Username}) con rotacion obligatoria en el primer acceso.", username);
        return true;
    }

    public async Task<(IReadOnlyList<WebNotificationRow> Items, int Total)> ListNotificationsAsync(
        string? status,
        string? targetType,
        IReadOnlyList<int>? sellerTargetIds,
        IReadOnlyList<int>? zoneTargetIds,
        int page,
        int perPage,
        CancellationToken cancellationToken = default)
    {
        var (where, parametros) = ConstruirFiltroNotificaciones(status, targetType, sellerTargetIds, zoneTargetIds);

        await using var conn = await AbrirAsync(cancellationToken);
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM web_notifications WHERE {where};";
        foreach (var (nombre, valor) in parametros)
            countCmd.Parameters.AddWithValue(nombre, valor);
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(cancellationToken) ?? 0);

        await using var listCmd = conn.CreateCommand();
        listCmd.CommandText = $"""
            SELECT id, target_type, target_id, title, body, priority, status,
                   sender_user_id, sender_username, idempotency_key, trace_id, created_at
            FROM web_notifications
            WHERE {where}
            ORDER BY created_at DESC, id DESC
            LIMIT $perPage OFFSET $offset;
            """;
        foreach (var (nombre, valor) in parametros)
            listCmd.Parameters.AddWithValue(nombre, valor);
        listCmd.Parameters.AddWithValue("$perPage", perPage);
        listCmd.Parameters.AddWithValue("$offset", (page - 1) * perPage);

        var items = new List<WebNotificationRow>();
        await using var reader = await listCmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(LeerNotificacion(reader));

        return (items, total);
    }

    public async Task<int> CountActiveNotificationsAsync(
        IReadOnlyList<int>? sellerTargetIds,
        IReadOnlyList<int>? zoneTargetIds,
        CancellationToken cancellationToken = default)
    {
        var (where, parametros) = ConstruirFiltroNotificaciones("active", null, sellerTargetIds, zoneTargetIds);

        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM web_notifications WHERE {where};";
        foreach (var (nombre, valor) in parametros)
            cmd.Parameters.AddWithValue(nombre, valor);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    public async Task<WebNotificationRow?> FindNotificationByIdempotencyAsync(string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, target_type, target_id, title, body, priority, status,
                   sender_user_id, sender_username, idempotency_key, trace_id, created_at
            FROM web_notifications
            WHERE idempotency_key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? LeerNotificacion(reader) : null;
    }

    public async Task<WebNotificationRow> CreateNotificationAsync(
        string targetType,
        int targetId,
        string title,
        string body,
        string priority,
        long? senderUserId,
        string? senderUsername,
        string idempotencyKey,
        string? traceId,
        CancellationToken cancellationToken = default)
    {
        var creado = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO web_notifications (target_type, target_id, title, body, priority, status,
                                           sender_user_id, sender_username, idempotency_key, trace_id, created_at)
            VALUES ($targetType, $targetId, $title, $body, $priority, 'active',
                    $senderUserId, $senderUsername, $key, $traceId, $creado);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$targetType", targetType);
        cmd.Parameters.AddWithValue("$targetId", targetId);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.Parameters.AddWithValue("$priority", priority);
        cmd.Parameters.AddWithValue("$senderUserId", (object?)senderUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$senderUsername", (object?)senderUsername ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$key", idempotencyKey);
        cmd.Parameters.AddWithValue("$traceId", (object?)traceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$creado", creado);
        var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);

        return new WebNotificationRow(id, targetType, targetId, title, body, priority, "active", senderUserId, senderUsername, idempotencyKey, traceId, creado);
    }

    public async Task<IReadOnlyList<WebNotificationRow>> CreateNotificationsBatchAsync(
        IReadOnlyList<NotificationBatchItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
            return Array.Empty<WebNotificationRow>();

        var creado = DateTime.UtcNow.ToString("o");
        var resultados = new List<WebNotificationRow>(items.Count);

        await using var conn = await AbrirAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO web_notifications (target_type, target_id, title, body, priority, status,
                                                   sender_user_id, sender_username, idempotency_key, trace_id, created_at)
                    VALUES ($targetType, $targetId, $title, $body, $priority, 'active',
                            $senderUserId, $senderUsername, $key, $traceId, $creado);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$targetType", item.TargetType);
                cmd.Parameters.AddWithValue("$targetId", item.TargetId);
                cmd.Parameters.AddWithValue("$title", item.Title);
                cmd.Parameters.AddWithValue("$body", item.Body);
                cmd.Parameters.AddWithValue("$priority", item.Priority);
                cmd.Parameters.AddWithValue("$senderUserId", (object?)item.SenderUserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$senderUsername", (object?)item.SenderUsername ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$key", (object?)item.IdempotencyKey ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$traceId", (object?)item.TraceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$creado", creado);
                var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
                resultados.Add(new WebNotificationRow(id, item.TargetType, item.TargetId, item.Title, item.Body, item.Priority, "active", item.SenderUserId, item.SenderUsername, item.IdempotencyKey, item.TraceId, creado));
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }

        return resultados;
    }

    /// <summary>
    /// Construye el WHERE de la bandeja. seller/zone restringen el alcance del
    /// usuario (listas vacías = alcance total); NULL = sin restricción.
    /// </summary>
    private static (string Where, List<(string Nombre, object Valor)> Parametros) ConstruirFiltroNotificaciones(
        string? status,
        string? targetType,
        IReadOnlyList<int>? sellerTargetIds,
        IReadOnlyList<int>? zoneTargetIds)
    {
        var condiciones = new List<string>();
        var parametros = new List<(string, object)>();

        if (!string.IsNullOrWhiteSpace(status))
        {
            condiciones.Add("status = $status");
            parametros.Add(("$status", status));
        }

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            condiciones.Add("target_type = $targetType");
            parametros.Add(("$targetType", targetType));
        }

        var restricciones = new List<string>();
        if (sellerTargetIds is { Count: > 0 })
        {
            var nombres = string.Join(", ", sellerTargetIds.Select((_, i) => $"$seller{i}"));
            for (int i = 0; i < sellerTargetIds.Count; i++)
                parametros.Add(($"$seller{i}", sellerTargetIds[i]));
            restricciones.Add($"(target_type IN ('seller', 'route') AND target_id IN ({nombres}))");
        }

        if (zoneTargetIds is { Count: > 0 })
        {
            var nombres = string.Join(", ", zoneTargetIds.Select((_, i) => $"$zone{i}"));
            for (int i = 0; i < zoneTargetIds.Count; i++)
                parametros.Add(($"$zone{i}", zoneTargetIds[i]));
            restricciones.Add($"(target_type = 'zone' AND target_id IN ({nombres}))");
        }

        if (restricciones.Count > 0)
            condiciones.Add($"({string.Join(" OR ", restricciones)})");

        return (condiciones.Count > 0 ? string.Join(" AND ", condiciones) : "1 = 1", parametros);
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
        reader.GetString(5),
        reader.GetInt64(6) != 0,
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt64(8) != 0,
        reader.GetString(9),
        reader.GetString(10));

    private static WebNotificationRow LeerNotificacion(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.GetString(11));
}