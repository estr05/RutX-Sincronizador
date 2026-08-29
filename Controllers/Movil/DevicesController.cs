using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

[ApiController]
[Route("api/v1/devices")]
public class DevicesController : ControllerBase
{
    private readonly IDeviceRegistryService _registryService;

    public DevicesController(IDeviceRegistryService registryService)
    {
        _registryService = registryService;
    }

    [HttpPost("activate")]
    [Authorize] // Auth simple de JWT móvil
    public async Task<IActionResult> Activate([FromBody] DeviceActivationRequest request, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst("vendedor_id")?.Value, out int sellerId))
        {
            return Forbid();
        }

        try
        {
            var response = await _registryService.ActivateDeviceAsync(request, sellerId, ct);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
