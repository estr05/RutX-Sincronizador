using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web.Inventory;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Piloto de inventario por ruta (contrato v2 §6.4, aprobado en el Bloque 0).
/// GET /api/v2/web/inventory/by-route — requiere permiso inventory.read.
/// Solo lectura: la fórmula aprobada (§12.2) vive en el servicio.
/// </summary>
[Authorize(Policy = "web.inventory.read")]
[ApiController]
[Route("api/v2/web")]
public class InventoryController : ControllerBase
{
    private readonly IInventoryWebService _inventoryWebService;
    private readonly ILogger<InventoryController> _logger;

    public InventoryController(IInventoryWebService inventoryWebService, ILogger<InventoryController> logger)
    {
        _inventoryWebService = inventoryWebService ?? throw new ArgumentNullException(nameof(inventoryWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("inventory/by-route")]
    public async Task<IActionResult> GetByRoute([FromQuery] InventoryRouteQuery query, CancellationToken ct)
    {
        try
        {
            var resultado = await _inventoryWebService.ListByRouteAsync(query, WebClaims.Zonas(User), ct);

            if (!resultado.IsSuccess)
                return StatusCode(StatusCodePara(resultado.Code), WebEnvelope.Error(HttpContext, resultado.Code!, resultado.Message!));

            return Ok(WebEnvelope.Success(HttpContext,
                resultado.Response!.Data,
                meta: new
                {
                    page = resultado.Response.Meta.Page,
                    per_page = resultado.Response.Meta.PerPage,
                    total = resultado.Response.Meta.Total,
                    last_page = resultado.Response.Meta.LastPage,
                },
                filters: resultado.Response.Filters));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/inventory/by-route");
            return StatusCode(500, WebEnvelope.Error(HttpContext, "API_UNAVAILABLE", "No se pudo conectar con el servicio."));
        }
    }

    private int StatusCodePara(string? code) => code switch
    {
        "FORBIDDEN_ZONE" => StatusCodes.Status403Forbidden,
        "VALIDATION_ERROR" => StatusCodes.Status422UnprocessableEntity,
        "NOT_FOUND" => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status503ServiceUnavailable,
    };
}