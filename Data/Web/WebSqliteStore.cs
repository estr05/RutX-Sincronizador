using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data.Sqlite;
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
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ILogger<WebSqliteStore> _logger;

    public WebSqliteStore(ISqliteConnectionFactory connectionFactory, ILogger<WebSqliteStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<int> EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        var migrator = new WebSqliteMigrator(_connectionFactory.ConnectionString, logger: _logger);
        return await migrator.ApplyAsync(cancellationToken);
    }

    public async Task<int> SchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        var migrator = new WebSqliteMigrator(_connectionFactory.ConnectionString, logger: _logger);
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

    // ────────────────────────────────────────────────────────────────────────
    // SAGA DE NO VENTAS (v004)
    // ────────────────────────────────────────────────────────────────────────

    public async Task<NoSaleOperationRow> CreateOrGetNoSaleOperationAsync(
        string ventaMovilId, string requestHash,
        int vendedorId, int clienteId, int causaId, string fechaHora,
        string? payloadJson = null, string? sessionJson = null,
        CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);

        // BEGIN IMMEDIATE garantiza serialización sin deadlock en WAL
        await using var cmd0 = conn.CreateCommand();
        cmd0.CommandText = "PRAGMA journal_mode=WAL;";
        await cmd0.ExecuteNonQueryAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct) as Microsoft.Data.Sqlite.SqliteTransaction
            ?? throw new InvalidOperationException("SqliteTransaction no disponible.");

        // INSERT OR IGNORE: si ya existe, no hace nada; no falla.
        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT OR IGNORE INTO rutx_no_sale_operations
                (venta_movil_id, request_hash, vendedor_id, cliente_id, causa_id,
                 fecha_hora, status, attempts, created_at, updated_at, payload_json, session_json)
            VALUES
                ($vmid, $hash, $vid, $cid, $causaId, $fh, 'pending', 0, $now, $now, $payload, $session);
            """;
        ins.Parameters.AddWithValue("$vmid",   ventaMovilId);
        ins.Parameters.AddWithValue("$hash",   requestHash);
        ins.Parameters.AddWithValue("$vid",    vendedorId);
        ins.Parameters.AddWithValue("$cid",    clienteId);
        ins.Parameters.AddWithValue("$causaId", causaId);
        ins.Parameters.AddWithValue("$fh",     fechaHora);
        ins.Parameters.AddWithValue("$now",    ahora);
        ins.Parameters.AddWithValue("$payload", (object?)payloadJson ?? DBNull.Value);
        ins.Parameters.AddWithValue("$session", (object?)sessionJson ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync(ct);

        // SELECT la fila existente o recién creada
        await using var sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT id, venta_movil_id, request_hash, vendedor_id, cliente_id, causa_id,
                   fecha_hora, docto_pv_id, folio, foto_file_id,
                   status, attempts, error_code, error_message, created_at, updated_at,
                   payload_json, session_json
            FROM rutx_no_sale_operations
            WHERE venta_movil_id = $vmid
            LIMIT 1;
            """;
        sel.Parameters.AddWithValue("$vmid", ventaMovilId);
        await using var reader = await sel.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var row = LeerNoSaleOperation(reader);

        await tx.CommitAsync(ct);
        return row;
    }

    public async Task<NoSaleOperationRow?> FindNoSaleOperationAsync(
        string ventaMovilId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, venta_movil_id, request_hash, vendedor_id, cliente_id, causa_id,
                   fecha_hora, docto_pv_id, folio, foto_file_id,
                   status, attempts, error_code, error_message, created_at, updated_at,
                   payload_json, session_json
            FROM rutx_no_sale_operations
            WHERE venta_movil_id = $vmid
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$vmid", ventaMovilId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? LeerNoSaleOperation(reader) : null;
    }

    public async Task<NoSaleOperationRow> UpdateNoSaleOperationAsync(
        long id, string status,
        int? doctoPvId = null, string? folio = null, long? fotoFileId = null,
        string? errorCode = null, string? errorMessage = null,
        CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE rutx_no_sale_operations
            SET status        = $status,
                docto_pv_id   = COALESCE($doctoPvId,  docto_pv_id),
                folio         = COALESCE($folio,       folio),
                foto_file_id  = COALESCE($fotoFileId, foto_file_id),
                error_code    = $errorCode,
                error_message = $errorMessage,
                attempts      = attempts + 1,
                updated_at    = $now
            WHERE id = $id
            RETURNING id, venta_movil_id, request_hash, vendedor_id, cliente_id, causa_id,
                      fecha_hora, docto_pv_id, folio, foto_file_id,
                      status, attempts, error_code, error_message, created_at, updated_at,
                      payload_json, session_json;
            """;
        cmd.Parameters.AddWithValue("$id",           id);
        cmd.Parameters.AddWithValue("$status",       status);
        cmd.Parameters.AddWithValue("$doctoPvId",    (object?)doctoPvId  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folio",        (object?)folio      ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fotoFileId",   (object?)fotoFileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorCode",    (object?)errorCode  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorMessage", (object?)errorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now",          ahora);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"No se encontró la operación con id={id} tras actualizar.");
        return LeerNoSaleOperation(reader);
    }

    public async Task<MediaFileRow> CreateMediaFileAsync(
        long operationId, string category,
        string? originalName, string storedName, string relativePath,
        string mimeType, long sizeBytes, string sha256,
        CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rutx_media_files
                (category, operation_id, original_name, stored_name, relative_path,
                 mime_type, size_bytes, sha256, status, created_at)
            VALUES
                ($cat, $opId, $origName, $stored, $relPath, $mime, $size, $sha, 'staging', $now)
            RETURNING id, category, operation_id, original_name, stored_name, relative_path,
                      mime_type, size_bytes, sha256, status, created_at, promoted_at;
            """;
        cmd.Parameters.AddWithValue("$cat",      category);
        cmd.Parameters.AddWithValue("$opId",     operationId);
        cmd.Parameters.AddWithValue("$origName", (object?)originalName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$stored",   storedName);
        cmd.Parameters.AddWithValue("$relPath",  relativePath);
        cmd.Parameters.AddWithValue("$mime",     mimeType);
        cmd.Parameters.AddWithValue("$size",     sizeBytes);
        cmd.Parameters.AddWithValue("$sha",      sha256);
        cmd.Parameters.AddWithValue("$now",      ahora);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("INSERT de media_file no retornó fila.");
        return LeerMediaFile(reader);
    }

    public async Task<MediaFileRow?> FindMediaFileByStoredNameAsync(
        string storedName, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, category, operation_id, original_name, stored_name, relative_path,
                   mime_type, size_bytes, sha256, status, created_at, promoted_at
            FROM rutx_media_files
            WHERE stored_name = $stored
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$stored", storedName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? LeerMediaFile(reader) : null;
    }

    public async Task<MediaFileRow?> FindMediaFileByIdAsync(
        long id, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, category, operation_id, original_name, stored_name, relative_path,
                   mime_type, size_bytes, sha256, status, created_at, promoted_at
            FROM rutx_media_files
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? LeerMediaFile(reader) : null;
    }

    public async Task UpdateMediaFileStatusAsync(
        long id, string status, string? relativePath = null, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE rutx_media_files
            SET status       = $status,
                relative_path = COALESCE($relPath, relative_path),
                promoted_at  = CASE WHEN $status = 'completed' THEN $now ELSE promoted_at END
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id",      id);
        cmd.Parameters.AddWithValue("$status",  status);
        cmd.Parameters.AddWithValue("$relPath", (object?)relativePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now",     ahora);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ────────────────────────────────────────────────────────────────────────
    // HELPERS DE LECTURA PRIVADOS
    // ────────────────────────────────────────────────────────────────────────

    private async Task<SqliteConnection> AbrirAsync(CancellationToken cancellationToken)
    {
        return await _connectionFactory.CreateConnectionAsync(cancellationToken);
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

    public async Task<List<NoSaleOperationRow>> ObtenerNoVentasParaReintentoAsync(
        int maxIntentos = 5,
        int limite = 10,
        CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, venta_movil_id, request_hash, vendedor_id, cliente_id, causa_id,
                   fecha_hora, docto_pv_id, folio, foto_file_id,
                   status, attempts, error_code, error_message, created_at, updated_at,
                   payload_json, session_json
            FROM rutx_no_sale_operations
            WHERE status IN ('pending', 'media_staged', 'media_promotion_pending', 'retryable_failed')
              AND attempts < $maxIntentos
            ORDER BY updated_at ASC
            LIMIT $limite;
            """;
        cmd.Parameters.AddWithValue("$maxIntentos", maxIntentos);
        cmd.Parameters.AddWithValue("$limite", limite);

        var resultados = new List<NoSaleOperationRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            resultados.Add(LeerNoSaleOperation(reader));
        }

        return resultados;
    }

    private static NoSaleOperationRow LeerNoSaleOperation(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3),
        r.GetInt32(4),
        r.GetInt32(5),
        r.GetString(6),
        r.IsDBNull(7)  ? null : r.GetInt32(7),
        r.IsDBNull(8)  ? null : r.GetString(8),
        r.IsDBNull(9)  ? null : r.GetInt64(9),
        r.GetString(10),
        r.GetInt32(11),
        r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.GetString(14),
        r.GetString(15),
        r.FieldCount > 16 && !r.IsDBNull(16) ? r.GetString(16) : null,
        r.FieldCount > 17 && !r.IsDBNull(17) ? r.GetString(17) : null);

    private static MediaFileRow LeerMediaFile(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        r.GetInt64(7),
        r.GetString(8),
        r.GetString(9),
        r.GetString(10),
        r.IsDBNull(11) ? null : r.GetString(11));

    // ─────────────────────────────────────────────────────────────────────────
    // TELEMETRÍA Y DISPOSITIVOS (v007)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<RutxContractRow?> GetContractByNumberAsync(string contractNumber, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, contract_number, license_number, license_key_hash, status, max_active_devices, valid_from, valid_to, created_at, updated_at FROM rutx_contracts WHERE contract_number = $cn LIMIT 1";
        cmd.Parameters.AddWithValue("$cn", contractNumber);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return LeerContract(r);
    }

    public async Task<RutxContractRow?> GetContractByIdAsync(long contractId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, contract_number, license_number, license_key_hash, status, max_active_devices, valid_from, valid_to, created_at, updated_at FROM rutx_contracts WHERE id = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", contractId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return LeerContract(r);
    }

    public async Task<RutxContractRow> EnsureContractAsync(string contractNumber, string validFrom, string? validTo, int maxActiveDevices, CancellationToken ct = default)
    {
        var existing = await GetContractByNumberAsync(contractNumber, ct);
        if (existing != null) return existing;

        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rutx_contracts (contract_number, status, max_active_devices, valid_from, valid_to, created_at, updated_at)
            VALUES ($cn, 'active', $max, $vf, $vt, $now, $now);
            """;
        cmd.Parameters.AddWithValue("$cn", contractNumber);
        cmd.Parameters.AddWithValue("$max", maxActiveDevices);
        cmd.Parameters.AddWithValue("$vf", validFrom);
        cmd.Parameters.AddWithValue("$vt", (object?)validTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ahora);
        await cmd.ExecuteNonQueryAsync(ct);
        return (await GetContractByNumberAsync(contractNumber, ct))!;
    }

    public async Task UpsertCustomerRefAsync(long contractId, int customerId, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO rutx_customer_refs (contract_id, customer_id, created_at) VALUES ($cid, $cust, $now)";
        cmd.Parameters.AddWithValue("$cid", contractId);
        cmd.Parameters.AddWithValue("$cust", customerId);
        cmd.Parameters.AddWithValue("$now", ahora);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> FindActiveCustomerRefAsync(long contractId, int customerId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM rutx_customer_refs WHERE contract_id = $cid AND customer_id = $cust LIMIT 1";
        cmd.Parameters.AddWithValue("$cid", contractId);
        cmd.Parameters.AddWithValue("$cust", customerId);
        var res = await cmd.ExecuteScalarAsync(ct);
        return res != null;
    }

    public async Task<RutxMobileDeviceRow> UpsertMobileDeviceAsync(string deviceId, string platform, string appVersion, string? tokenHash, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rutx_mobile_devices (device_id, platform, app_version, token_hash, created_at, updated_at)
            VALUES ($did, $plat, $appv, $thash, $now, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                app_version = excluded.app_version,
                updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$did", deviceId);
        cmd.Parameters.AddWithValue("$plat", platform);
        cmd.Parameters.AddWithValue("$appv", appVersion);
        cmd.Parameters.AddWithValue("$thash", (object?)tokenHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ahora);
        await cmd.ExecuteNonQueryAsync(ct);
        return (await GetMobileDeviceAsync(deviceId, ct))!;
    }

    public async Task<RutxMobileDeviceRow?> GetMobileDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, device_id, platform, app_version, token_hash, created_at, updated_at FROM rutx_mobile_devices WHERE device_id = $did LIMIT 1";
        cmd.Parameters.AddWithValue("$did", deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return LeerDevice(r);
    }

    public async Task<RutxDeviceAssignmentRow?> GetActiveAssignmentAsync(string deviceId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.device_id, a.contract_id, a.device_number, a.seller_id, a.status, a.valid_from, a.valid_to, a.created_at, a.updated_at
            FROM rutx_device_assignments a
            JOIN rutx_mobile_devices d ON a.device_id = d.id
            WHERE d.device_id = $did AND a.status = 'active' AND a.valid_to IS NULL
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$did", deviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return LeerAssignment(r);
    }

    public async Task<RutxDeviceAssignmentRow> CreateAssignmentAsync(long deviceRowId, long contractId, int deviceNumber, int sellerId, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rutx_device_assignments (device_id, contract_id, device_number, seller_id, status, valid_from, created_at, updated_at)
            VALUES ($did, $cid, $num, $sid, 'active', $now, $now, $now);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$did", deviceRowId);
        cmd.Parameters.AddWithValue("$cid", contractId);
        cmd.Parameters.AddWithValue("$num", deviceNumber);
        cmd.Parameters.AddWithValue("$sid", sellerId);
        cmd.Parameters.AddWithValue("$now", ahora);
        var id = (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
        return new RutxDeviceAssignmentRow(id, deviceRowId, contractId, deviceNumber, sellerId, "active", ahora, null, ahora, ahora);
    }

    public async Task RevokeAssignmentAsync(long assignmentId, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rutx_device_assignments SET status = 'revoked', valid_to = $now, updated_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", ahora);
        cmd.Parameters.AddWithValue("$id", assignmentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountActiveAssignmentsAsync(long contractId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rutx_device_assignments WHERE contract_id = $cid AND status = 'active' AND valid_to IS NULL";
        cmd.Parameters.AddWithValue("$cid", contractId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<(string Result, SellerEventRow Row)> RegisterEventAsync(SellerEventRow evt, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var txn = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = txn;
            insertCmd.CommandText = """
                INSERT OR IGNORE INTO rutx_seller_events
                    (client_event_id, event_type, seller_id, device_assignment_id,
                     customer_id, related_entity_id, payload_hash, metadata_json,
                     occurred_at, status, created_at, updated_at)
                VALUES ($ceid, $etype, $sid, $daid, $cid, $reid, $phash, $meta,
                        $oat, 'received', $now, $now);
                """;
            insertCmd.Parameters.AddWithValue("$ceid", evt.ClientEventId);
            insertCmd.Parameters.AddWithValue("$etype", evt.EventType);
            insertCmd.Parameters.AddWithValue("$sid", evt.SellerId);
            insertCmd.Parameters.AddWithValue("$daid", (object?)evt.DeviceAssignmentId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$cid", (object?)evt.CustomerId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$reid", (object?)evt.RelatedEntityId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$phash", evt.PayloadHash);
            insertCmd.Parameters.AddWithValue("$meta", (object?)evt.MetadataJson ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$oat", evt.OccurredAt);
            insertCmd.Parameters.AddWithValue("$now", ahora);
            var changes = await insertCmd.ExecuteNonQueryAsync(ct);

            if (changes == 1)
            {
                await txn.CommitAsync(ct);
                var row = await GetEventByClientEventIdAsync(evt.ClientEventId, ct);
                return ("inserted", row!);
            }

            var existing = await GetEventByClientEventIdAsync(evt.ClientEventId, ct);
            await txn.CommitAsync(ct);
            if (existing!.PayloadHash == evt.PayloadHash)
                return ("duplicate_same_hash", existing);
            return ("conflict", existing);
        }
        catch
        {
            await txn.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<SellerEventRow?> GetEventByClientEventIdAsync(string clientEventId, CancellationToken ct = default)
    {
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, client_event_id, event_type, seller_id, device_assignment_id,
                   customer_id, related_entity_id, payload_hash, metadata_json,
                   occurred_at, status, created_at, updated_at
            FROM rutx_seller_events
            WHERE client_event_id = $ceid LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$ceid", clientEventId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return LeerEvent(r);
    }

    public async Task<SellerEventRow> UpdateEventStatusAsync(long eventId, string status, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rutx_seller_events SET status = $status, updated_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", ahora);
        cmd.Parameters.AddWithValue("$id", eventId);
        await cmd.ExecuteNonQueryAsync(ct);
        
        await using var getCmd = conn.CreateCommand();
        getCmd.CommandText = """
            SELECT id, client_event_id, event_type, seller_id, device_assignment_id,
                   customer_id, related_entity_id, payload_hash, metadata_json,
                   occurred_at, status, created_at, updated_at
            FROM rutx_seller_events
            WHERE id = $id LIMIT 1
            """;
        getCmd.Parameters.AddWithValue("$id", eventId);
        await using var r = await getCmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return LeerEvent(r);
    }

    public async Task InsertLocationForEventAsync(
        long eventId, int sellerId, int? sessionId,
        double latitude, double longitude, double? accuracy,
        string occurredAt, string? sourceEventType, int? customerId,
        CancellationToken ct = default)
    {
        if (latitude < -90.0 || latitude > 90.0) throw new ArgumentException("Latitud fuera de rango.");
        if (longitude < -180.0 || longitude > 180.0) throw new ArgumentException("Longitud fuera de rango.");
        if (accuracy.HasValue && accuracy.Value < 0) throw new ArgumentException("Precision negativa.");

        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var txn = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            long? resolvedSessionId = sessionId;
            if (resolvedSessionId == null)
            {
                await using var sesCmd = conn.CreateCommand();
                sesCmd.Transaction = txn;
                sesCmd.CommandText = "SELECT id FROM rutx_seller_sessions WHERE seller_id = $sid AND status = 'open' ORDER BY started_at DESC LIMIT 1";
                sesCmd.Parameters.AddWithValue("$sid", sellerId);
                var res = await sesCmd.ExecuteScalarAsync(ct);
                if (res != null && res != DBNull.Value)
                {
                    resolvedSessionId = Convert.ToInt64(res);
                }
                else
                {
                    // Auto-crear sesión de vendedor si no existe una abierta
                    await using var createSesCmd = conn.CreateCommand();
                    createSesCmd.Transaction = txn;
                    createSesCmd.CommandText = """
                        INSERT INTO rutx_seller_sessions (seller_id, status, started_at, created_at)
                        VALUES ($sid, 'open', $now, $now);
                        SELECT last_insert_rowid();
                    """;
                    createSesCmd.Parameters.AddWithValue("$sid", sellerId);
                    createSesCmd.Parameters.AddWithValue("$now", ahora);
                    resolvedSessionId = (long)(await createSesCmd.ExecuteScalarAsync(ct) ?? 0L);
                }
            }

            await using var insertCmd = conn.CreateCommand();
            insertCmd.Transaction = txn;
            insertCmd.CommandText = """
                INSERT INTO rutx_seller_locations 
                    (session_id, seller_id, latitude, longitude, accuracy, recorded_at, created_at, event_id, source_event_type, customer_id)
                VALUES ($sesid, $sid, $lat, $lon, $acc, $rec, $now, $evid, $set, $cid)
                """;
            insertCmd.Parameters.AddWithValue("$sesid", (object?)resolvedSessionId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$sid", sellerId);
            insertCmd.Parameters.AddWithValue("$lat", latitude);
            insertCmd.Parameters.AddWithValue("$lon", longitude);
            insertCmd.Parameters.AddWithValue("$acc", (object?)accuracy ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$rec", occurredAt);
            insertCmd.Parameters.AddWithValue("$now", ahora);
            insertCmd.Parameters.AddWithValue("$evid", eventId);
            insertCmd.Parameters.AddWithValue("$set", (object?)sourceEventType ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("$cid", (object?)customerId ?? DBNull.Value);
            
            await insertCmd.ExecuteNonQueryAsync(ct);
            await txn.CommitAsync(ct);
        }
        catch
        {
            await txn.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<RouteClosureRow> CreateOrGetRouteClosureAsync(string cierreMovilId, int sellerId, string? requestJson, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO rutx_route_closures (cierre_movil_id, seller_id, request_json, status, created_at, updated_at)
            VALUES ($cmid, $sid, $req, 'pending', $now, $now);
            """;
        cmd.Parameters.AddWithValue("$cmid", cierreMovilId);
        cmd.Parameters.AddWithValue("$sid", sellerId);
        cmd.Parameters.AddWithValue("$req", (object?)requestJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ahora);
        await cmd.ExecuteNonQueryAsync(ct);

        await using var getCmd = conn.CreateCommand();
        getCmd.CommandText = "SELECT id, cierre_movil_id, seller_id, request_json, response_json, status, created_at, updated_at FROM rutx_route_closures WHERE cierre_movil_id = $cmid LIMIT 1";
        getCmd.Parameters.AddWithValue("$cmid", cierreMovilId);
        await using var r = await getCmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return LeerClosure(r);
    }

    public async Task<RouteClosureRow> UpdateRouteClosureAsync(long id, string status, string? responseJson, CancellationToken ct = default)
    {
        var ahora = DateTime.UtcNow.ToString("o");
        await using var conn = await AbrirAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE rutx_route_closures SET status = $status, response_json = $res, updated_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$res", (object?)responseJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", ahora);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);

        await using var getCmd = conn.CreateCommand();
        getCmd.CommandText = "SELECT id, cierre_movil_id, seller_id, request_json, response_json, status, created_at, updated_at FROM rutx_route_closures WHERE id = $id LIMIT 1";
        getCmd.Parameters.AddWithValue("$id", id);
        await using var r = await getCmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return LeerClosure(r);
    }

    private static RutxContractRow LeerContract(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4), r.GetInt32(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetString(9));

    private static RutxMobileDeviceRow LeerDevice(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6));

    private static RutxDeviceAssignmentRow LeerAssignment(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetString(9));

    private static SellerEventRow LeerEvent(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12));

    private static RouteClosureRow LeerClosure(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));
}