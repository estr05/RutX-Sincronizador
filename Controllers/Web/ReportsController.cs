using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Reportes del portal web (contrato v2 §6.3).
/// Rutas bajo /api/v2/web/* — NO tocan el contrato móvil /api/v1/*.
/// Flujo: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
[Authorize(Policy = "web.any")]
[ApiController]
[Route("api/v2/web/reports")]
public class ReportsController : ControllerBase
{
    private readonly IReportsWebService _reportsWebService;
    private readonly ILogger<ReportsController> _logger;

    public ReportsController(IReportsWebService reportsWebService, ILogger<ReportsController> logger)
    {
        _reportsWebService = reportsWebService ?? throw new ArgumentNullException(nameof(reportsWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/v2/web/reports/sales → SalesReportResponse (agregados por ruta).
    /// </summary>
    [HttpGet("sales")]
    public async Task<IActionResult> GetSales([FromQuery] ReportFilterQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _reportsWebService.ObtenerReporteVentasAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/reports/sales");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    /// <summary>
    /// GET /api/v2/web/reports/sales-comparison → ComparisonResponse (dos series).
    /// </summary>
    [HttpGet("sales-comparison")]
    public async Task<IActionResult> GetSalesComparison([FromQuery] ReportFilterQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _reportsWebService.ObtenerComparativaAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/reports/sales-comparison");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    /// <summary>
    /// GET /api/v2/web/reports/route-profitability → rentabilidad por ruta.
    /// Prioridad Posterior (§6.3): se activa solo si el cliente lo contrata.
    /// </summary>
    [HttpGet("route-profitability")]
    public async Task<IActionResult> GetRouteProfitability([FromQuery] ReportFilterQuery filtros, CancellationToken ct)
    {
        try
        {
            var data = await _reportsWebService.ObtenerRentabilidadPorRutaAsync(filtros, ct);
            return Ok(WebEnvelope.Success(HttpContext, data, filters: filtros));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/reports/route-profitability");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }
}
