// ============================================================================
// ARCHIVO: CobranzaController.cs
// PROPOSITO: Controlador REST para registrar cobranzas (pago de creditos).
//
// ENDPOINT: POST /api/v1/cobranza/insert
//   Recibe una cobranza (pago de venta a credito) y la registra creando:
//   - DOCTOS_PV (TIPO_DOCTO='P')
//   - DOCTOS_PV_COBROS (forma(s) de pago)
//   - DOCTOS_PV_LIGAS (vinculo con venta(s) original)
//   - DOCTOS_CC (abono en CxC)
//   - DOCTOS_ENTRE_SIS (vinculo PV->CC)
//
// DIFERENCIA CON VentasPvController:
//   VentasPvController -> Crea ventas NUEVAS (TIPO_DOCTO='V')
//   CobranzaController -> Crea PAGOS de ventas existentes (TIPO_DOCTO='P')
// ============================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

[Authorize]
[ApiController]
[Route("api/v1/cobranza")]
public class CobranzaController : ControllerBase
{
    private readonly ICobranzaService _cobranzaService;
    private readonly ILogger<CobranzaController> _logger;

    public CobranzaController(ICobranzaService cobranzaService, ILogger<CobranzaController> logger)
    {
        _cobranzaService = cobranzaService ?? throw new ArgumentNullException(nameof(cobranzaService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// POST /api/v1/cobranza/insert
    /// Registra una cobranza (pago de venta a credito).
    ///
    /// EJEMPLO DE REQUEST:
    /// {
    ///   "cobranza_movil_id": "cob-001",
    ///   "vendedor_id": 695,
    ///   "cliente_id": 431,
    ///   "fecha_hora": "2026-07-28T10:30:00",
    ///   "pagos": [
    ///     { "forma_cobro_id": 67, "importe": 500.00 }
    ///   ],
    ///   "documentos_cobrar": [
    ///     { "docto_pv_original_id": 1452071, "importe_pagado": 500.00 }
    ///   ]
    /// }
    /// </summary>
    [HttpPost("insert")]
    public async Task<IActionResult> InsertCobranza([FromBody] CobranzaCreateDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var response = await _cobranzaService.InsertCobranzaAsync(dto);
            return StatusCode(201, response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ERROR COBRANZA] {Message}", ex.Message);
            return StatusCode(500, new
            {
                message = "Error al registrar la cobranza",
                error = ex.Message
            });
        }
    }
}
