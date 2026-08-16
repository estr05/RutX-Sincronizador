using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Notifications;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Bandeja y emisión de notificaciones (contrato v2 §6.4).
/// - GET  /api/v2/web/notifications         → notifications.read
/// - GET  /api/v2/web/notifications/count   → notifications.read (campana)
/// - POST /api/v2/web/notifications         → notifications.send
///   Idempotency-Key obligatorio (422 si falta; 409 si se repite).
/// El alcance por zonas del usuario se aplica en el servicio.
/// </summary>
[ApiController]
[Route("api/v2/web")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationWebService _notificationWebService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(INotificationWebService notificationWebService, ILogger<NotificationsController> logger)
    {
        _notificationWebService = notificationWebService ?? throw new ArgumentNullException(nameof(notificationWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("notifications")]
    [Authorize(Policy = "web.notifications.read")]
    public async Task<IActionResult> GetNotifications([FromQuery] NotificationListQuery query, CancellationToken ct)
    {
        try
        {
            var resultado = await _notificationWebService.ListAsync(query, WebClaims.Zonas(User), ct);

            if (!resultado.IsSuccess)
                return StatusCode(StatusCodePara(resultado.Code), WebEnvelope.Error(resultado.Code!, resultado.Message!));

            var lista = (WebListResponse<NotificationListItemDto>)resultado.Response!;
            return Ok(WebEnvelope.Success(
                lista.Data,
                meta: new { page = lista.Meta.Page, per_page = lista.Meta.PerPage, total = lista.Meta.Total, last_page = lista.Meta.LastPage },
                filters: lista.Filters));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/notifications");
            return StatusCode(500, WebEnvelope.Error("API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    [HttpGet("notifications/count")]
    [Authorize(Policy = "web.notifications.read")]
    public async Task<IActionResult> GetCount(CancellationToken ct)
    {
        try
        {
            var total = await _notificationWebService.CountActiveAsync(WebClaims.Zonas(User), ct);
            return Ok(WebEnvelope.Success(new NotificationCountDto(total)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/notifications/count");
            return StatusCode(500, WebEnvelope.Error("API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    [HttpPost("notifications")]
    [Authorize(Policy = "web.notifications.send")]
    public async Task<IActionResult> Create([FromBody] NotificationCreateRequest request, CancellationToken ct)
    {
        try
        {
            var idempotencyKey = Request.Headers["Idempotency-Key"].ToString();
            var traceId = HttpContext.Items["trace_id"]?.ToString();
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            var resultado = await _notificationWebService.CreateAsync(
                request,
                string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                WebClaims.UserId(User),
                WebClaims.Username(User),
                WebClaims.Zonas(User),
                traceId,
                ip,
                ct);

            if (!resultado.IsSuccess)
                return StatusCode(StatusCodePara(resultado.Code), WebEnvelope.Error(resultado.Code!, resultado.Message!));

            return StatusCode(StatusCodes.Status201Created, WebEnvelope.Success(resultado.Response!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en POST /api/v2/web/notifications");
            return StatusCode(500, WebEnvelope.Error("API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    private int StatusCodePara(string? code) => code switch
    {
        "FORBIDDEN_ZONE" => StatusCodes.Status403Forbidden,
        "IDEMPOTENCY_CONFLICT" => StatusCodes.Status409Conflict,
        "VALIDATION_ERROR" => StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status503ServiceUnavailable,
    };
}