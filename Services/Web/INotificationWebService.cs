using Rutx.Sincronizador.Models.Web.Notifications;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Bandeja y emisión de notificaciones (contrato v2 §6.4).
/// Alcance: el destinatario es vendedor/ruta/zona; la bandeja se limita en
/// servidor a las zonas autorizadas del usuario (claims zone_ids).
/// </summary>
public interface INotificationWebService
{
    /// <summary>Bandeja paginada del usuario (alcance por sus zonas).</summary>
    Task<WebNotificationResult> ListAsync(
        NotificationListQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default);

    /// <summary>Contador de avisos activos en el alcance del usuario (campana).</summary>
    Task<int> CountActiveAsync(IReadOnlyList<int> userZoneIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Emite avisos a vendedor(es)/ruta(s)/zona(s) con idempotencia:
    /// Idempotency-Key obligatorio; si la clave ya se usó → IDEMPOTENCY_CONFLICT.
    /// Las zonas destino deben estar dentro de las zonas del usuario.
    /// </summary>
    Task<WebNotificationResult> CreateAsync(
        NotificationCreateRequest request,
        string? idempotencyKey,
        long? senderUserId,
        string? senderUsername,
        IReadOnlyList<int> userZoneIds,
        string? traceId,
        string? ipAddress,
        CancellationToken cancellationToken = default);
}

public sealed record WebNotificationResult(
    bool IsSuccess,
    string? Code,
    string? Message,
    object? Response = null)
{
    public static WebNotificationResult Exito(object response) => new(true, null, null, response);

    public static WebNotificationResult Error(string code, string message) => new(false, code, message, null);
}