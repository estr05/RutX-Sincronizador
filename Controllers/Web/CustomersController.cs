using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models.Web.Customers;
using Rutx.Sincronizador.Services.Web;

namespace Rutx.Sincronizador.Controllers.Web;

/// <summary>
/// Catálogo de clientes del portal (contrato v2 §6.2).
/// GET /api/v2/web/customers — requiere permiso customers.read.
/// route_id ≡ VENDEDOR_ID; la autorización de zona vive en el servicio.
/// </summary>
[Authorize(Policy = "web.customers.read")]
[ApiController]
[Route("api/v2/web")]
public class CustomersController : ControllerBase
{
    private readonly ICustomerWebService _customerWebService;
    private readonly ILogger<CustomersController> _logger;

    public CustomersController(ICustomerWebService customerWebService, ILogger<CustomersController> logger)
    {
        _customerWebService = customerWebService ?? throw new ArgumentNullException(nameof(customerWebService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("customers")]
    public async Task<IActionResult> GetCustomers([FromQuery] CustomerListQuery query, CancellationToken ct)
    {
        try
        {
            var resultado = await _customerWebService.ListAsync(query, WebClaims.Zonas(User), ct);

            if (!resultado.IsSuccess)
                return StatusCode(StatusCodePara(resultado.Code), WebEnvelope.Error(resultado.Code!, resultado.Message!));

            return Ok(WebEnvelope.Success(resultado.Response!.Data, meta: new { page = resultado.Response.Meta.Page, per_page = resultado.Response.Meta.PerPage, total = resultado.Response.Meta.Total, last_page = resultado.Response.Meta.LastPage }, filters: resultado.Response.Filters));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en GET /api/v2/web/customers");
            return StatusCode(500, WebEnvelope.Error("API_UNAVAILABLE", "No se pudo conectar con el servicio."));
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