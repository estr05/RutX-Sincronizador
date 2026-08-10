using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Admin;

/// <summary>
/// Endpoints de mantenimiento de folios (FOLIOS_CAJAS).
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/folios")]
public class FoliosController : ControllerBase
{
    private readonly IFolioService _folioService;
    private readonly ILogger<FoliosController> _logger;

    public FoliosController(IFolioService folioService, ILogger<FoliosController> logger)
    {
        _folioService = folioService ?? throw new ArgumentNullException(nameof(folioService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// POST /api/v1/folios/reparar
    /// Reparación única de contadores desfasados: alinea CONSECUTIVO de las
    /// filas V/P con MAX(folio emitido) + 1. Genera un respaldo en disco
    /// antes de modificar.
    /// </summary>
    [HttpPost("reparar")]
    public async Task<IActionResult> RepararContadores()
    {
        try
        {
            int corregidas = await _folioService.RepararContadoresAsync();
            return Ok(new
            {
                message = corregidas > 0
                    ? $"Contadores revisados: {corregidas} fila(s) corregida(s). Revisa Data/respaldo_contadores_*.json."
                    : "Contadores revisados: no se encontraron desfases.",
                filas_corregidas = corregidas
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FOLIOS] Error al reparar contadores: {Message}", ex.Message);
            return StatusCode(500, new { message = "No se pudo reparar los contadores.", error = ex.Message });
        }
    }
}
