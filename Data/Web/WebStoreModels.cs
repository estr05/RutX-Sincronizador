namespace Rutx.Sincronizador.Data.Web;

/// <summary>
/// Registro de un usuario de oficina (tabla web_users).
/// </summary>
public sealed record WebUserRow(
    long Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string RolesJson,
    bool MustChangePassword,
    string? PasswordChangedAt,
    bool IsActive,
    string CreatedAt,
    string UpdatedAt);

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