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