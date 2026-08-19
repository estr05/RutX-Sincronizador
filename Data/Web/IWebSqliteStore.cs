namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Almacén de la BD complementaria web (SQLite). TODO el esquema se crea
/// mediante migraciones versionadas (WebSqliteMigrator); ninguna clase de
/// negocio ejecuta CREATE TABLE.
/// </summary>
public interface IWebSqliteStore
{
    /// <summary>Aplica migraciones pendientes (idempotente) y devuelve la versión vigente.</summary>
    Task<int> EnsureSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>Versión vigente del esquema sin aplicar nada.</summary>
    Task<int> SchemaVersionAsync(CancellationToken cancellationToken = default);

    Task<WebUserRow?> FindUserByUsernameAsync(string username, CancellationToken cancellationToken = default);

    Task<WebUserRow?> FindUserByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Crea un usuario con su contraseña ya hasheada (WebPasswordHasher).</summary>
    Task<WebUserRow> CreateUserAsync(
        string username,
        string displayName,
        string passwordHash,
        string rolesJson,
        bool mustChangePassword,
        string zoneIdsJson = "[]",
        CancellationToken cancellationToken = default);

    /// <summary>Actualiza el hash de contraseña (rotación tras NecesitaRehash o cambio).</summary>
    Task UpdateUserPasswordAsync(long userId, string passwordHash, CancellationToken cancellationToken = default);

    Task<int> CountUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea el administrador inicial SOLO desde configuración externa
    /// (WebAuth:AdminUsername/AdminPassword). Ver WebSqliteStore.EnsureAdminSeedAsync.
    /// </summary>
    Task<bool> EnsureAdminSeedAsync(IConfiguration configuration, CancellationToken cancellationToken = default);

    /// <summary>Vendedores (VENDEDOR_ID de Microsip) asignados a una zona.</summary>
    Task<List<int>> SellersForZoneAsync(int zoneId, CancellationToken cancellationToken = default);

    /// <summary>Zonas autorizadas para un vendedor (para validación cruzada).</summary>
    Task<List<int>> ZonesForSellerAsync(int sellerId, CancellationToken cancellationToken = default);

    Task<WebAuditEntry> LogAuditAsync(
        long? userId,
        string? username,
        string action,
        string? detail,
        string? ipAddress,
        string? traceId,
        CancellationToken cancellationToken = default);

    /// <summary>Bandeja paginada; los listados de destino restringen el alcance por zonas del usuario.</summary>
    Task<(IReadOnlyList<WebNotificationRow> Items, int Total)> ListNotificationsAsync(
        string? status,
        string? targetType,
        IReadOnlyList<int>? sellerTargetIds,
        IReadOnlyList<int>? zoneTargetIds,
        int page,
        int perPage,
        CancellationToken cancellationToken = default);

    /// <summary>Contador de avisos activos en el alcance del usuario (campana del topbar).</summary>
    Task<int> CountActiveNotificationsAsync(
        IReadOnlyList<int>? sellerTargetIds,
        IReadOnlyList<int>? zoneTargetIds,
        CancellationToken cancellationToken = default);

    /// <summary>Un aviso por Idempotency-Key (detección de reintentos del comando).</summary>
    Task<WebNotificationRow?> FindNotificationByIdempotencyAsync(string idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>Crea un aviso (una fila por destinatario) con su clave de idempotencia y traza.</summary>
    Task<WebNotificationRow> CreateNotificationAsync(
        string targetType,
        int targetId,
        string title,
        string body,
        string priority,
        long? senderUserId,
        string? senderUsername,
        string idempotencyKey,
        string? traceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un lote de avisos en una ÚNICA transacción SQLite (atomicidad de lote).
    /// Devuelve las filas creadas en orden. Si la transacción falla, NO crea ninguna fila
    /// y lanza excepción (rollback automático).
    /// </summary>
    Task<IReadOnlyList<WebNotificationRow>> CreateNotificationsBatchAsync(
        IReadOnlyList<NotificationBatchItem> items,
        CancellationToken cancellationToken = default);
}

/// <summary>Datos para una fila del lote de notificaciones.</summary>
public sealed record NotificationBatchItem(
    string TargetType,
    int TargetId,
    string Title,
    string Body,
    string Priority,
    long? SenderUserId,
    string? SenderUsername,
    string? IdempotencyKey,
    string? TraceId);