using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

[ApiController]
[Route("api/v1/telemetry")]
public class TelemetryController : ControllerBase
{
    private readonly ITelemetryService _telemetryService;

    public TelemetryController(ITelemetryService telemetryService)
    {
        _telemetryService = telemetryService;
    }

    [HttpPost("events")]
    [Authorize]
    public async Task<IActionResult> PostEvent([FromBody] TelemetryEventRequest request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst("vendedor_id")?.Value, out int sellerId))
            return Forbid();

        try
        {
            var response = await _telemetryService.ProcessEventAsync(request, sellerId, ct);
            return Ok(response);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(403, new { error = "device_not_authorized" });
        }
        catch (ArgumentException)
        {
            return UnprocessableEntity(new { error = "request_invalid" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "event_conflict" });
        }
    }
}
