using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Monitoreo de rutas del portal web (contrato v2 §6.1).
/// Rutas bajo /api/v2/web/* — NO tocan el contrato móvil /api/v1/*.
/// Flujo: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
[Authorize(Policy = "web.any")]
[ApiController]
[Route("api/v2/web")]
public class RouteMonitorController : ControllerBase
{
    private readonly IRouteMonitoringWebService _routeMonitoringWebService;
    private readonly ILogger<RouteMonitorController> _logger;

    public RouteMonitorController(IRouteMonitoringWebService routeMonitoringWebService, ILogger<RouteMonitorController> logger)
    {
        _routeMonitoringWebService = routeMonitoringWebService ?? throw new ArgumentNullException(nameof(routeMonitoringWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/v2/web/route-monitor → RouteMonitorListResponse (mapa en vivo).
    /// </summary>
    [HttpGet("route-monitor")]
    public async Task<IActionResult> GetMonitor([FromQuery] RouteMonitorQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _routeMonitoringWebService.ObtenerMonitoreoAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/route-monitor");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    /// <summary>
    /// GET /api/v2/web/route-monitor/{route_id} → RouteTimelineResponse.
    /// </summary>
    [HttpGet("route-monitor/{routeId:int}")]
    public async Task<IActionResult> GetRouteDetail(int routeId, CancellationToken ct)
    {
        try
        {
            var data = await _routeMonitoringWebService.ObtenerDetalleRutaAsync(routeId, ct);
            return Ok(WebEnvelope.Success(HttpContext, data));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/route-monitor/{RouteId}", routeId);
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    /// <summary>
    /// GET /api/v2/web/routes → catálogo paginado de rutas para filtros.
    /// </summary>
    [HttpGet("routes")]
    public async Task<IActionResult> GetRoutes([FromQuery] RouteMonitorQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _routeMonitoringWebService.ObtenerCatalogoRutasAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, meta: new { page = 1, per_page = 25, total = data.Count, last_page = 1 }, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/routes");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }
}
