namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Registro de un usuario de oficina (tabla web_users).
/// ZoneIdsJson: JSON de zonas autorizadas; vacío = sin restricción de zona.
/// </summary>
public sealed record WebUserRow(
    long Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string RolesJson,
    string ZoneIdsJson,
    bool MustChangePassword,
    string? PasswordChangedAt,
    bool IsActive,
    string CreatedAt,
    string UpdatedAt);

/// <summary>
/// Registro de una notificación de la bandeja (tabla web_notifications).
/// target_type ∈ {seller, route, zone}; una fila por destinatario.
/// </summary>
public sealed record WebNotificationRow(
    long Id,
    string TargetType,
    int TargetId,
    string Title,
    string Body,
    string Priority,
    string Status,
    long? SenderUserId,
    string? SenderUsername,
    string? IdempotencyKey,
    string? TraceId,
    string CreatedAt);

/// <summary>
/// Registro de auditoría (tabla web_audit_log).
/// </summary>
public sealed record WebAuditEntry(
    long Id,
    long? UserId,
    string? Username,
    string Action,
    string? Detail,
    string? IpAddress,
    string? TraceId,
    string CreatedAt);

/// <summary>
/// Operación de no-venta en la saga (tabla rutx_no_sale_operations).
/// Estados: received → media_staged → firebird_committed → completed | failed.
/// </summary>
public sealed record NoSaleOperationRow(
    long Id,
    string VentaMovilId,
    string RequestHash,
    int VendedorId,
    int ClienteId,
    int CausaId,
    string FechaHora,
    int? DoctoPvId,
    string? Folio,
    long? FotoFileId,
    string Status,
    int Attempts,
    string? ErrorCode,
    string? ErrorMessage,
    string CreatedAt,
    string UpdatedAt,
    string? PayloadJson = null,
    string? SessionJson = null);

/// <summary>
/// Archivo multimedia registrado en BD/C (tabla rutx_media_files).
/// stored_name: nombre físico generado por el servidor (nunca del cliente).
/// Estados: staging → completed | failed.
/// </summary>
public sealed record MediaFileRow(
    long Id,
    string Category,
    long? OperationId,
    string? OriginalName,
    string StoredName,
    string RelativePath,
    string MimeType,
    long SizeBytes,
    string Sha256,
    string Status,
    string CreatedAt,
    string? PromotedAt);

// ── v007 Telemetría ──────────────────────────────────────────────────────────

public sealed record RutxContractRow(
    long Id,
    string ContractNumber,
    string? LicenseNumber,
    string? LicenseKeyHash,
    string Status,
    int MaxActiveDevices,
    string ValidFrom,
    string? ValidTo,
    string CreatedAt,
    string UpdatedAt);

public sealed record RutxCustomerRefRow(
    long Id,
    long ContractId,
    int CustomerId,
    string CreatedAt);

public sealed record RutxMobileDeviceRow(
    long Id,
    string DeviceId,
    string Platform,
    string AppVersion,
    string? TokenHash,
    string CreatedAt,
    string UpdatedAt);

public sealed record RutxDeviceAssignmentRow(
    long Id,
    long DeviceId,
    long ContractId,
    int DeviceNumber,
    int SellerId,
    string Status,
    string ValidFrom,
    string? ValidTo,
    string CreatedAt,
    string UpdatedAt);

public sealed record SellerEventRow(
    long Id,
    string ClientEventId,
    string EventType,
    int SellerId,
    long? DeviceAssignmentId,
    int? CustomerId,
    string? RelatedEntityId,
    string PayloadHash,
    string? MetadataJson,
    string OccurredAt,
    string Status,
    string CreatedAt,
    string UpdatedAt);

public sealed record RouteClosureRow(
    long Id,
    string CierreMovilId,
    int SellerId,
    string? RequestJson,
    string? ResponseJson,
    string Status,
    string CreatedAt,
    string UpdatedAt);

public sealed record NotificationRecipientRow(
    long Id,
    long ContractId,
    int? SellerId,
    long? DeviceId,
    string? PushToken,
    string CreatedAt);

public sealed record NotificationDeliveryRow(
    long Id,
    long RecipientId,
    long? NotificationId,
    string Status,
    string? SentAt,
    string? Error,
    string CreatedAt);

public sealed record NotificationReceiptRow(
    long Id,
    long DeliveryId,
    string ReceivedAt,
    string CreatedAt);

public sealed record RouteAssignmentRow(
    long Id,
    long ContractId,
    int SellerId,
    string RouteDate,
    string Status,
    string? Notes,
    string CreatedAt,
    string UpdatedAt);