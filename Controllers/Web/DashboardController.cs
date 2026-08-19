using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Tablero del portal web (contrato v2 §6.1).
/// Rutas bajo /api/v2/web/* — NO tocan el contrato móvil /api/v1/*.
/// Flujo: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v2/web")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardWebService _dashboardWebService;
    private readonly ILogger<DashboardController> _logger;

    public DashboardController(IDashboardWebService dashboardWebService, ILogger<DashboardController> logger)
    {
        _dashboardWebService = dashboardWebService ?? throw new ArgumentNullException(nameof(dashboardWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/v2/web/dashboard → DashboardSummaryResponse (7 KPIs + meta).
    /// </summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard([FromQuery] ReportFilterQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _dashboardWebService.ObtenerResumenAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, meta: new { last_sync_at = data.Meta.LastSyncAt, currency = data.Meta.Currency }, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/dashboard");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    /// <summary>
    /// GET /api/v2/web/dashboard/sales-series → SalesSeriesResponse.
    /// </summary>
    [HttpGet("dashboard/sales-series")]
    public async Task<IActionResult> GetSalesSeries([FromQuery] ReportFilterQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _dashboardWebService.ObtenerSerieVentasAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/dashboard/sales-series");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }
}
