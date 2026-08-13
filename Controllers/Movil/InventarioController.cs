using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

/// <summary>
/// Endpoints de reconciliacion de inventario (movimientos de Microsip).
/// Clasifica las operaciones de los almacenes (TRANSFER / SEED / ANOMALIA)
/// y expone las anomalias para su revision.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/inventario")]
public class InventarioController : ControllerBase
{
    private readonly IInventarioReconciliacionService _reconciliacionService;
    private readonly ILogger<InventarioController> _logger;

    public InventarioController(
        IInventarioReconciliacionService reconciliacionService,
        ILogger<InventarioController> logger)
    {
        _reconciliacionService = reconciliacionService ?? throw new ArgumentNullException(nameof(reconciliacionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/v1/inventario/reconciliacion?desde=yyyy-MM-dd
    /// Ejecuta la reconciliacion desde la fecha indicada (default: ultimos 7 dias)
    /// y devuelve las operaciones clasificadas y las anomalias detectadas.
    /// </summary>
    [HttpGet("reconciliacion")]
    public async Task<IActionResult> Reconciliar([FromQuery] DateTime? desde)
    {
        try
        {
            var desdeConsulta = desde ?? DateTime.Today.AddDays(-7);
            var resultado = await _reconciliacionService.ReconciliarAsync(desdeConsulta);

            return Ok(new
            {
                mensaje = "Reconciliacion completada",
                generado_en = resultado.GeneradoEn,
                desde = resultado.Desde.ToString("yyyy-MM-dd"),
                resumen = new
                {
                    transferencias = resultado.Transferencias,
                    semillas = resultado.Semillas,
                    anomalias = resultado.Anomalias
                },
                operaciones = resultado.Operaciones,
                anomalias = resultado.AnomaliasList
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en reconciliacion de inventario");
            return StatusCode(500, new { mensaje = "Error al reconciliar inventario.", detalle = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/v1/inventario/reconciliacion/ultima
    /// Devuelve el resultado de la ultima corrida (la alimenta el servicio de
    /// fondo periodico), sin re-consultar la BD.
    /// </summary>
    [HttpGet("reconciliacion/ultima")]
    public IActionResult UltimaReconciliacion()
    {
        var ultima = _reconciliacionService.UltimoResultado;
        if (ultima == null)
            return Ok(new { mensaje = "Aún no hay una corrida de reconciliación. Consulta /reconciliacion para ejecutar una." });

        return Ok(new
        {
            generado_en = ultima.GeneradoEn,
            desde = ultima.Desde.ToString("yyyy-MM-dd"),
            resumen = new
            {
                transferencias = ultima.Transferencias,
                semillas = ultima.Semillas,
                anomalias = ultima.Anomalias
            },
            anomalias = ultima.AnomaliasList
        });
    }
}
