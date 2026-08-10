// ============================================================================
// ARCHIVO: CreditoController.cs
// PROPOSITO: Controlador REST para consultar creditos pendientes de clientes.
//
// ENDPOINT: GET /api/v1/credito/pedidos
//   Retorna la lista de clientes con saldo pendiente en CxC, incluyendo:
//   - Saldo actual
//   - Limite de credito
//   - Porcentaje usado
//   - Documentos pendientes
//   - Dias de atraso
//
// FUENTE DE DATOS:
//   SALDOS_CC: Saldos mensuales de CxC por cliente
//   CLIENTES:  Datos del cliente y limite de credito
//   VENCIMIENTOS_CARGOS_CC: Fechas de vencimiento para calcular atraso
// ============================================================================

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

[Authorize]
[ApiController]
[Route("api/v1/credito")]
public class CreditoController : ControllerBase
{
    private readonly ICreditoService _creditoService;

    public CreditoController(ICreditoService creditoService)
    {
        _creditoService = creditoService ?? throw new ArgumentNullException(nameof(creditoService));
    }

    /// <summary>
    /// GET /api/v1/credito/pedidos
    /// Obtiene la lista de creditos pendientes de pago.
    ///
    /// PARAMETROS OPCIONALES:
    ///   cliente_id  : Filtrar por cliente especifico
    ///   vendedor_id : Filtrar por vendedor
    ///
    /// EJEMPLOS:
    ///   GET /api/v1/credito/pedidos
    ///   GET /api/v1/credito/pedidos?cliente_id=431
    ///   GET /api/v1/credito/pedidos?vendedor_id=695
    /// </summary>
    [HttpGet("pedidos")]
    public async Task<IActionResult> GetPedidosCredito(
        [FromQuery] int? cliente_id,
        [FromQuery] int? vendedor_id)
    {
        try
        {
            var response = await _creditoService.SelectPedidosCreditoAsync(
                clienteId: cliente_id,
                vendedorId: vendedor_id ?? 0);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                mensaje = "Error al consultar creditos pendientes.",
                detalle = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/v1/credito/clientes/{id}/documentos
    /// Obtiene los documentos de venta 'V' pendientes de pago para un cliente.
    ///
    /// RELACION CON TRIGGERS:
    ///   DOCTOS_PV_AFTUPD_0 -> APLICA_DOCTO_PV -> APLICA_VTA_PV
    ///     -> GENERA_DOCTO_CC_PV crea DOCTOS_CC (cargo) + DOCTOS_ENTRE_SIS
    ///
    ///   Cada documento devuelto representa una venta a credito cuyo cargo
    ///   en DOCTOS_CC aun no ha sido pagado completamente.
    ///   El saldo pendiente se calcula como:
    ///     cargo_original_en_CxC - SUM(abonos_aplicados)
    ///
    /// EJEMPLO:
    ///   GET /api/v1/credito/clientes/431/documentos
    /// </summary>
    [HttpGet("clientes/{id}/documentos")]
    public async Task<IActionResult> GetDocumentosCliente(int id)
    {
        try
        {
            var response = await _creditoService.SelectDocumentosClienteAsync(id);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                mensaje = "Error al consultar documentos del cliente.",
                detalle = ex.Message
            });
        }
    }
}
