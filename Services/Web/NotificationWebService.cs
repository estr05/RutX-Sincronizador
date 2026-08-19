using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Notifications;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Implementación de INotificationWebService sobre WebSqliteStore.
///
/// Idempotencia: el encabezado Idempotency-Key es obligatorio (sin él la API
/// responde VALIDATION_ERROR, contrato v2 §6.4). Una clave repetida devuelve
/// IDEMPOTENCY_CONFLICT aunque el cuerpo coincida (política estricta).
///
/// Zonas: los avisos a zona(s) solo se permiten para zonas dentro de
/// zone_ids del usuario; avisos a vendedor/ruta no se restringen por zona
/// (la ruta es la unidad operativa y el envío es una acción de oficina).
/// </summary>
public sealed class NotificationWebService : INotificationWebService
{
    private static readonly string[] PrioridadesValidas = { "normal", "alta", "urgente" };
    private static readonly string[] TiposDestinoValidos = { "seller", "route", "zone" };

    private readonly IWebSqliteStore _store;
    private readonly ILogger<NotificationWebService> _logger;

    public NotificationWebService(IWebSqliteStore store, ILogger<NotificationWebService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WebNotificationResult> ListAsync(
        NotificationListQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default)
    {
        var (page, perPage) = NormalizarPaginacion(query.Page, query.PerPage);

        var (items, total) = await _store.ListNotificationsAsync(
            query.Status,
            query.TargetType,
            null,
            userZoneIds,
            page,
            perPage,
            cancellationToken);

        var dto = items.Select(n => new NotificationListItemDto(
            n.Id,
            n.TargetType,
            n.TargetId,
            n.Title,
            n.Body,
            n.Priority,
            n.Status,
            n.SenderUsername,
            n.CreatedAt)).ToList();

        return WebNotificationResult.Exito(new WebListResponse<NotificationListItemDto>(
            dto,
            WebPageMeta.Create(page, perPage, total),
            new { page, per_page = perPage, status = query.Status, target_type = query.TargetType }));
    }

    public async Task<int> CountActiveAsync(IReadOnlyList<int> userZoneIds, CancellationToken cancellationToken = default)
        => await _store.CountActiveNotificationsAsync(null, userZoneIds, cancellationToken);

    public async Task<WebNotificationResult> CreateAsync(
        NotificationCreateRequest request,
        string? idempotencyKey,
        long? senderUserId,
        string? senderUsername,
        IReadOnlyList<int> userZoneIds,
        string? traceId,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return WebNotificationResult.Error("VALIDATION_ERROR", "El encabezado Idempotency-Key es obligatorio para emitir notificaciones.");

        if (!TiposDestinoValidos.Contains(request.TargetType, StringComparer.Ordinal))
            return WebNotificationResult.Error("VALIDATION_ERROR", "target_type debe ser seller, route o zone.");

        if (request.TargetIds is not { Count: > 0 } || request.TargetIds.Count > NotificationCreateRequest.MaxTargetIds)
            return WebNotificationResult.Error("VALIDATION_ERROR", $"Se requiere entre 1 y {NotificationCreateRequest.MaxTargetIds} destinatarios.");

        var title = request.Title?.Trim();
        var body = request.Body?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title) || title.Length > NotificationCreateRequest.MaxTitleLength)
            return WebNotificationResult.Error("VALIDATION_ERROR", $"title es requerido (máx. {NotificationCreateRequest.MaxTitleLength} caracteres).");
        if (body.Length > NotificationCreateRequest.MaxBodyLength)
            return WebNotificationResult.Error("VALIDATION_ERROR", $"body no puede exceder {NotificationCreateRequest.MaxBodyLength} caracteres.");

        if (!PrioridadesValidas.Contains(request.Priority, StringComparer.Ordinal))
            return WebNotificationResult.Error("VALIDATION_ERROR", "priority debe ser normal, alta o urgente.");

        if (request.TargetType == "zone" && userZoneIds.Count > 0)
        {
            var fueraDeZonas = request.TargetIds.Except(userZoneIds).ToArray();
            if (fueraDeZonas.Length > 0)
                return WebNotificationResult.Error("FORBIDDEN_ZONE", "Solo puedes emitir avisos a zonas dentro de las zonas autorizadas de tu usuario.");
        }

        var existente = await _store.FindNotificationByIdempotencyAsync(idempotencyKey, cancellationToken);
        if (existente is not null)
        {
            _logger.LogWarning("Idempotency-Key repetida '{Key}' en emisión de notificaciones por '{Username}'", idempotencyKey, senderUsername);
            return WebNotificationResult.Error("IDEMPOTENCY_CONFLICT", "Esta Idempotency-Key ya fue utilizada para una emisión previa.");
        }

        var creadas = new List<long>();
        var first = true;
        foreach (var targetId in request.TargetIds.Distinct())
        {
            var fila = await _store.CreateNotificationAsync(
                request.TargetType,
                targetId,
                title,
                body,
                request.Priority,
                senderUserId,
                senderUsername,
                first ? idempotencyKey : string.Empty,
                traceId,
                cancellationToken);
            creadas.Add(fila.Id);
            first = false;
        }

        _logger.LogInformation("Notificaciones emitidas ({Count}) por '{Username}' (type={Type}, key={Key})",
            creadas.Count, senderUsername, request.TargetType, idempotencyKey);

        return WebNotificationResult.Exito(new NotificationCreateResult(
            creadas.Count,
            creadas,
            request.TargetType));
    }

    private static (int Page, int PerPage) NormalizarPaginacion(int page, int perPage)
    {
        var p = Math.Clamp(page, NotificationListQuery.MinPage, int.MaxValue);
        var pp = Math.Clamp(perPage, NotificationListQuery.MinPerPage, NotificationListQuery.MaxPerPage);
        return (p, pp);
    }
}