namespace Rutx.Sincronizador.Models.Web.Notifications;

/// <summary>
/// Consulta de la bandeja GET /api/v2/web/notifications (contrato v2 §6.4).
/// El alcance por usuario/rol/zona lo aplica el servicio en servidor.
/// </summary>
public sealed record NotificationListQuery(
    int Page = 1,
    int PerPage = 25,
    string? Status = null,
    string? TargetType = null)
{
    public const int MinPage = 1;
    public const int MinPerPage = 1;
    public const int MaxPerPage = 100;
}

/// <summary>
/// Fila de la bandeja. target_type ∈ {seller, route, zone}; route ≡ seller
/// (VENDEDOR_ID) según la decisión §12.1, y el tipo se conserva tal como se
/// emitió. El marcado como leído queda fuera del alcance (PATCH /read es
/// posterior); status refleja el ciclo de vida del aviso ('active').
/// </summary>
public sealed record NotificationListItemDto(
    long Id,
    string TargetType,
    int TargetId,
    string Title,
    string Body,
    string Priority,
    string Status,
    string? SenderUsername,
    string CreatedAt);

/// <summary>
/// Request de POST /api/v2/web/notifications: emisión a vendedor(es),
/// ruta(s) o zona(s). Máximo de destinatarios por comando validado en el
/// servicio; la idempotencia la garantiza el encabezado Idempotency-Key.
/// </summary>
public sealed record NotificationCreateRequest(
    string TargetType,
    IReadOnlyList<int> TargetIds,
    string Title,
    string Body,
    string Priority = "normal")
{
    public const int MaxTargetIds = 50;
    public const int MaxTitleLength = 200;
    public const int MaxBodyLength = 1000;
}

/// <summary>
/// Respuesta de POST /api/v2/web/notifications: avisos creados (una fila
/// por destinatario). Tipo concreto (no anónimo) para que los consumidores
/// puedan deserializarlo sin reflexión del ensamblado del servicio.
/// </summary>
public sealed record NotificationCreateResult(
    int CreatedCount,
    IReadOnlyList<long> NotificationIds,
    string TargetType);

/// <summary>
/// Respuesta de GET /api/v2/web/notifications/count (campana del topbar).
/// </summary>
public sealed record NotificationCountDto(int ActiveCount);