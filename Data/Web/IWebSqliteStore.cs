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

    /// <summary>
    /// Crea un aviso (una fila por destinatario) con su clave de idempotencia y traza.
    /// </summary>
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

    // ────────────────────────────────────────────────────────────────────────
    // SAGA DE NO VENTAS — rutx_no_sale_operations + rutx_media_files (v004)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Crea la operación de no-venta con estado 'received', o devuelve la existente
    /// si venta_movil_id ya existe (UNIQUE). La operación es atómica:
    /// INSERT OR IGNORE + SELECT en una transacción con BEGIN IMMEDIATE.
    /// </summary>
    Task<NoSaleOperationRow> CreateOrGetNoSaleOperationAsync(
        string ventaMovilId,
        string requestHash,
        int vendedorId,
        int clienteId,
        int causaId,
        string fechaHora,
        string? payloadJson = null,
        string? sessionJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// Busca una operación de no-venta por su venta_movil_id.
    /// Devuelve null si no existe.
    /// </summary>
    Task<NoSaleOperationRow?> FindNoSaleOperationAsync(
        string ventaMovilId,
        CancellationToken ct = default);

    /// <summary>
    /// Actualiza el estado y campos opcionales de una operación de no-venta.
    /// Incrementa attempts automáticamente.
    /// </summary>
    Task<NoSaleOperationRow> UpdateNoSaleOperationAsync(
        long id,
        string status,
        int? doctoPvId = null,
        string? folio = null,
        long? fotoFileId = null,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken ct = default);

    /// <summary>
    /// Registra metadata de un archivo multimedia en estado 'staging'.
    /// stored_name es UNIQUE; si ya existe, lanza excepción (no-op no deseado).
    /// </summary>
    Task<MediaFileRow> CreateMediaFileAsync(
        long operationId,
        string category,
        string? originalName,
        string storedName,
        string relativePath,
        string mimeType,
        long sizeBytes,
        string sha256,
        CancellationToken ct = default);

    /// <summary>
    /// Busca un archivo multimedia por su stored_name.
    /// Devuelve null si no existe.
    /// </summary>
    Task<MediaFileRow?> FindMediaFileByStoredNameAsync(
        string storedName,
        CancellationToken ct = default);

    /// <summary>
    /// Busca un archivo multimedia por su ID.
    /// Devuelve null si no existe.
    /// </summary>
    Task<MediaFileRow?> FindMediaFileByIdAsync(
        long id,
        CancellationToken ct = default);

    /// <summary>
    /// Actualiza el estado de un archivo multimedia (ej: staging → completed).
    /// Opcionalmente actualiza la ruta relativa (para el paso de promoción).
    /// </summary>
    Task UpdateMediaFileStatusAsync(
        long id,
        string status,
        string? relativePath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Obtiene las operaciones de no-venta que deben ser reintentadas.
    /// </summary>
    Task<List<NoSaleOperationRow>> ObtenerNoVentasParaReintentoAsync(
        int maxIntentos = 5,
        int limite = 10,
        CancellationToken ct = default);

    // ─────────────────────────────────────────────────────────────────────────
    // TELEMETRÍA Y DISPOSITIVOS (v007)
    // ─────────────────────────────────────────────────────────────────────────

    // Contratos
    Task<RutxContractRow?> GetContractByNumberAsync(string contractNumber, CancellationToken ct = default);
    Task<RutxContractRow?> GetContractByIdAsync(long contractId, CancellationToken ct = default);
    Task<RutxContractRow> EnsureContractAsync(string contractNumber, string validFrom,
        string? validTo, int maxActiveDevices, CancellationToken ct = default);

    // Referencias de clientes
    Task UpsertCustomerRefAsync(long contractId, int customerId, CancellationToken ct = default);
    Task<bool> FindActiveCustomerRefAsync(long contractId, int customerId, CancellationToken ct = default);

    // Dispositivos
    Task<RutxMobileDeviceRow> UpsertMobileDeviceAsync(string deviceId, string platform,
        string appVersion, string? tokenHash, CancellationToken ct = default);
    Task<RutxMobileDeviceRow?> GetMobileDeviceAsync(string deviceId, CancellationToken ct = default);

    // Asignaciones
    Task<RutxDeviceAssignmentRow?> GetActiveAssignmentAsync(string deviceId, CancellationToken ct = default);
    Task<RutxDeviceAssignmentRow> CreateAssignmentAsync(long deviceRowId, long contractId,
        int deviceNumber, int sellerId, CancellationToken ct = default);
    Task RevokeAssignmentAsync(long assignmentId, CancellationToken ct = default);
    Task<int> CountActiveAssignmentsAsync(long contractId, CancellationToken ct = default);

    // Eventos (idempotentes)
    /// <summary>
    /// Resultado: "inserted" | "duplicate_same_hash" | "conflict"
    /// </summary>
    Task<(string Result, SellerEventRow Row)> RegisterEventAsync(
        SellerEventRow evt, CancellationToken ct = default);
    Task<SellerEventRow?> GetEventByClientEventIdAsync(string clientEventId, CancellationToken ct = default);
    Task<SellerEventRow> UpdateEventStatusAsync(long eventId, string status, CancellationToken ct = default);

    // Ubicaciones vinculadas a evento
    /// <summary>
    /// Inserta la ubicación en la misma transacción que el evento.
    /// Solo si lat/lon son válidas. session_id se resuelve desde la sesión activa del seller.
    /// </summary>
    Task InsertLocationForEventAsync(
        long eventId, int sellerId, int? sessionId,
        double latitude, double longitude, double? accuracy,
        string occurredAt, string? sourceEventType, int? customerId,
        CancellationToken ct = default);

    // Cierres de jornada
    Task<RouteClosureRow> CreateOrGetRouteClosureAsync(string cierreMovilId, int sellerId,
        string? requestJson, CancellationToken ct = default);
    Task<RouteClosureRow> UpdateRouteClosureAsync(long id, string status,
        string? responseJson, CancellationToken ct = default);
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