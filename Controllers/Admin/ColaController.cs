using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Admin;

[Rutx.Sincronizador.Security.AdminAuth]
[ApiController]
[Route("api/v2/admin/[controller]")]
public class ColaController : ControllerBase
{
    private readonly IColaOfflineService _colaService;

    public ColaController(IColaOfflineService colaService)
    {
        _colaService = colaService;
    }

    /// <summary>
    /// POST /api/v1/queue
    /// Registra una operación en la cola offline.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> AgregarOperacion([FromBody] ColaRequestDto request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var operacion = await _colaService.AgregarOperacionAsync(
                request.TipoOperacion,
                request.Idempotencia,
                request.Payload);

            return StatusCode(201, new
            {
                operacion_id = operacion.OperacionId,
                estado = operacion.Estado.ToString(),
                mensaje = "Operación registrada en cola"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al registrar operación en cola", error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/v1/queue/status/{operacionId}
    /// Consulta el estado de una operación en la cola.
    /// </summary>
    [HttpGet("status/{operacionId}")]
    public async Task<IActionResult> ObtenerEstado(string operacionId)
    {
        var operacion = await _colaService.ObtenerEstadoAsync(operacionId);

        if (operacion == null)
            return NotFound(new { message = "Operación no encontrada" });

        return Ok(new
        {
            operacion_id = operacion.OperacionId,
            estado = operacion.Estado.ToString(),
            intentos = operacion.Intentos,
            max_intentos = operacion.MaxIntentos,
            error_ultimo_intento = operacion.ErrorUltimoIntento,
            fecha_creacion = operacion.FechaCreacion,
            fecha_modificacion = operacion.FechaModificacion
        });
    }

    /// <summary>
    /// GET /api/v1/queue/failed
    /// Lista operaciones en estado FALLIDO.
    /// </summary>
    [HttpGet("failed")]
    public async Task<IActionResult> ObtenerFallidas()
    {
        var fallidas = await _colaService.ObtenerPorEstadoAsync(EstadoOperacion.FALLIDO, limite: 100);
        var total = await _colaService.ContarPorEstadoAsync(EstadoOperacion.FALLIDO);

        return Ok(new
        {
            total_fallidas = total,
            operaciones = fallidas.Select(o => new
            {
                operacion_id = o.OperacionId,
                tipo = o.TipoOperacion.ToString(),
                intentos = o.Intentos,
                max_intentos = o.MaxIntentos,
                error = o.ErrorUltimoIntento,
                fecha_creacion = o.FechaCreacion,
                fecha_modificacion = o.FechaModificacion
            })
        });
    }

    /// <summary>
    /// POST /api/v1/queue/retry/{operacionId}
    /// Reintenta manualmente una operación fallida.
    /// </summary>
    [HttpPost("retry/{operacionId}")]
    public async Task<IActionResult> Reintentar(string operacionId)
    {
        var operacion = await _colaService.ObtenerEstadoAsync(operacionId);

        if (operacion == null)
            return NotFound(new { message = "Operación no encontrada" });

        if (operacion.Estado != EstadoOperacion.FALLIDO)
            return BadRequest(new { message = $"La operación está en estado {operacion.Estado}, solo se pueden reintentar operaciones FALLIDO" });

        operacion.Estado = EstadoOperacion.PENDIENTE;
        operacion.Intentos = 0;
        operacion.SiguienteReintento = DateTime.UtcNow;
        operacion.ErrorUltimoIntento = null;

        await _colaService.ProcesarOperacionAsync(operacion);

        return Ok(new
        {
            operacion_id = operacion.OperacionId,
            estado = operacion.Estado.ToString(),
            mensaje = "Operación reintentada"
        });
    }

    /// <summary>
    /// DELETE /api/v1/queue/{operacionId}
    /// Elimina una operación de la cola (solo COMPLETADO o FALLIDO).
    /// </summary>
    [HttpDelete("{operacionId}")]
    public async Task<IActionResult> Eliminar(string operacionId)
    {
        var operacion = await _colaService.ObtenerEstadoAsync(operacionId);

        if (operacion == null)
            return NotFound(new { message = "Operación no encontrada" });

        if (operacion.Estado != EstadoOperacion.COMPLETADO && operacion.Estado != EstadoOperacion.FALLIDO)
            return BadRequest(new { message = "Solo se pueden eliminar operaciones COMPLETADO o FALLIDO" });

        await _colaService.EliminarAsync(operacionId);

        return Ok(new { operacion_id = operacionId, mensaje = "Operación eliminada" });
    }
}

public class ColaRequestDto
{
    public TipoOperacion TipoOperacion { get; set; }
    public string Idempotencia { get; set; } = string.Empty;
    public object Payload { get; set; } = new();
}
