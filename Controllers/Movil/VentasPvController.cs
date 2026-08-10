using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

/// <summary>
/// Controlador para el modulo de Punto de Venta (PV).
/// Endpoints para registrar, aplicar, cancelar y consultar ventas en DOCTOS_PV.
///
/// DIFERENCIAS CON VentasController:
///   VentasController -> DOCTOS_VE (Pedidos, modulo Ventas)
///   VentasPvController -> DOCTOS_PV (Tickets, modulo Punto de Venta)
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/pv")]
public class VentasPvController : ControllerBase
{
    private readonly IVentaServicePv _ventaServicePv;
    private readonly IColaOfflineRepository _colaRepository;
    private readonly ILogger<VentasPvController> _logger;

    public VentasPvController(
        IVentaServicePv ventaServicePv,
        IColaOfflineRepository colaRepository,
        ILogger<VentasPvController> logger)
    {
        _ventaServicePv = ventaServicePv ?? throw new ArgumentNullException(nameof(ventaServicePv));
        _colaRepository = colaRepository ?? throw new ArgumentNullException(nameof(colaRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// POST /api/v1/pv/ventas
    /// Registra una venta en DOCTOS_PV (Punto de Venta).
    /// Inserta cabecera + detalle + impuestos + cobros en una transaccion.
    /// Los triggers BEFINS generan los IDs automaticamente.
    ///
    /// IDEMPOTENCIA: si el móvil reintenta una venta ya registrada (mismo
    /// venta_movil_id), se devuelve la respuesta original sin duplicar el
    /// documento en Microsip.
    ///
    /// La identidad (caja, cajero, almacen, sucursal, usuario creador) se
    /// resuelve del token JWT (login nativo), no de la request.
    /// </summary>
    [HttpPost("ventas")]
    public async Task<IActionResult> RegistrarVenta([FromBody] VentaPvCreateDto ventaDto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (ventaDto.Detalles == null || ventaDto.Detalles.Count == 0)
            return BadRequest(new { message = "La venta debe contener al menos un articulo." });

        if (string.IsNullOrWhiteSpace(ventaDto.VentaMovilId))
            return BadRequest(new { message = "Falta venta_movil_id para garantizar la integridad de la venta." });

        var sesion = ObtenerSesionDeClaims();
        if (sesion == null)
            return Unauthorized(new { message = "Token inválido: faltan datos de sesión." });

        // IDEMPOTENCIA: si ya está registrada, reutilizar la respuesta original.
        var existente = await _colaRepository.ObtenerVentaSincronizadaAsync(ventaDto.VentaMovilId);
        if (existente != null)
        {
            if (existente.Estado == "COMPLETADO" && existente.DoctoPvId > 0)
            {
                _logger.LogInformation(
                    "[PV] Venta {VentaMovilId} ya registrada (idempotencia): DoctoPvId={DoctoPvId}, Folio={Folio}",
                    ventaDto.VentaMovilId, existente.DoctoPvId, existente.Folio);

                return StatusCode(201, new VentaPvResponseDto
                {
                    Message = "La venta ya había sido registrada.",
                    VentaMovilId = ventaDto.VentaMovilId,
                    DoctoPvId = existente.DoctoPvId.Value,
                    Folio = existente.Folio ?? "",
                    TotalRenglones = ventaDto.Detalles.Count,
                    Reutilizada = true
                });
            }

            return Conflict(new { message = "La venta ya está siendo procesada. Intenta de nuevo en unos momentos." });
        }

        // Reservar marcador PROCESANDO para evitar duplicados concurrentes.
        bool reservado = await _colaRepository.ReservarVentaAsync(ventaDto.VentaMovilId);
        if (!reservado)
            return Conflict(new { message = "La venta ya está siendo procesada. Intenta de nuevo en unos momentos." });

        try
        {
            var response = await _ventaServicePv.RegistrarVentaPvAsync(sesion, ventaDto);
            await _colaRepository.CompletarVentaAsync(ventaDto.VentaMovilId, response.DoctoPvId, response.Folio);
            return StatusCode(201, response);
        }
        catch (ArgumentException ex)
        {
            await _colaRepository.LiberarVentaAsync(ventaDto.VentaMovilId);
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            await _colaRepository.LiberarVentaAsync(ventaDto.VentaMovilId);
            return BadRequest(new { message = ex.Message });
        }
        catch (DeadlockTransientException ex)
        {
            // Conflicto transitorio: se libera para que la cola lo reintente.
            await _colaRepository.LiberarVentaAsync(ventaDto.VentaMovilId);
            _logger.LogWarning(ex, "[PV] Conflicto transitorio al registrar venta {VentaMovilId}", ventaDto.VentaMovilId);
            return StatusCode(503, new { message = "El servidor está momentáneamente ocupado. La venta se intentará de nuevo automáticamente." });
        }
        catch (Exception ex)
        {
            await _colaRepository.LiberarVentaAsync(ventaDto.VentaMovilId);
            _logger.LogError(ex, "[ERROR PV] {Message}", ex.Message);
            return StatusCode(500, new { message = "Error al registrar la venta en Punto de Venta", error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/v1/pv/noventa
    /// Registra una No Venta en DOCTOS_PV con TIPO_DOCTO = 'V'
    /// (estatus 'N' + prefijo 'NO VENTA:' en la descripcion).
    /// La identidad se resuelve del token JWT.
    /// </summary>
    [HttpPost("noventa")]
    public async Task<IActionResult> RegistrarNoVenta([FromBody] NoVentaPvCreateDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var sesion = ObtenerSesionDeClaims();
            if (sesion == null)
                return Unauthorized(new { message = "Token inválido: faltan datos de sesión." });

            var response = await _ventaServicePv.RegistrarNoVentaPvAsync(sesion, dto);
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
            _logger.LogError(ex, "[ERROR NO VENTA] {Message}", ex.Message);
            return StatusCode(500, new { message = "Error al registrar la no venta", error = ex.Message });
        }
    }

    /// <summary>
    /// Reconstruye la identidad del usuario desde los claims del token JWT.
    /// Returns null si falta informacion critica (vendedor/caja).
    /// </summary>
    private UsuarioSesion? ObtenerSesionDeClaims()
    {
        var vendedorId = ObtenerClaimEntero("vendedor_id");
        if (vendedorId <= 0)
            return null;

        return new UsuarioSesion
        {
            Usuario = User.FindFirst("usuario")?.Value ?? "",
            VendedorId = vendedorId,
            VendedorNombre = User.FindFirst("vendedor_nombre")?.Value ?? "VENDEDOR",
            CajeroId = ObtenerClaimEntero("cajero_id"),
            CajaId = ObtenerClaimEntero("caja_id"),
            AlmacenId = ObtenerClaimEntero("almacen_id"),
            SucursalId = ObtenerClaimEntero("sucursal_id")
        };
    }

    private int ObtenerClaimEntero(string tipo)
    {
        var valor = User.FindFirst(tipo)?.Value;
        return int.TryParse(valor, out var n) ? n : 0;
    }

    /// <summary>
    /// POST /api/v1/pv/ventas/{id}/aplicar
    /// Aplica la venta (afecta inventarios y CxC).
    /// Dispara trigger DOCTOS_PV_AFTUPD_0 -> APLICA_DOCTO_PV -> APLICA_VTA_PV.
    /// </summary>
    [HttpPost("ventas/{id:int}/aplicar")]
    public async Task<IActionResult> AplicarVenta(int id)
    {
        try
        {
            bool result = await _ventaServicePv.AplicarVentaAsync(id);
            if (!result)
                return BadRequest(new { message = $"No se pudo aplicar la venta {id}. Verifique que exista y no este ya aplicada." });

            return Ok(new { message = $"Venta {id} aplicada exitosamente" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al aplicar la venta", error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/v1/pv/ventas/{id}/cancelar
    /// Cancela la venta. Dispara trigger DOCTOS_PV_BEFUPD_0 que valida
    /// restricciones y ejecuta DESAPLICA_DOCTO_PV automaticamente.
    /// </summary>
    [HttpPost("ventas/{id:int}/cancelar")]
    public async Task<IActionResult> CancelarVenta(int id)
    {
        try
        {
            bool result = await _ventaServicePv.CancelarVentaAsync(id);
            if (!result)
                return BadRequest(new { message = $"No se pudo cancelar la venta {id}. Verifique que exista y no este ya cancelada." });

            return Ok(new { message = $"Venta {id} cancelada exitosamente" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al cancelar la venta", error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/v1/pv/ventas/{id}
    /// Consulta un ticket completo con JOIN de 5+ tablas.
    /// </summary>
    [HttpGet("ventas/{id:int}")]
    public async Task<IActionResult> ConsultarTicket(int id)
    {
        try
        {
            var ticket = await _ventaServicePv.ConsultarTicketAsync(id);
            var lista = ticket.ToList();

            if (!lista.Any())
                return NotFound(new { message = $"No se encontro el ticket con ID {id}" });

            return Ok(new
            {
                message = "Ticket consultado exitosamente",
                ticket = lista
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "Error al consultar el ticket", error = ex.Message });
        }
    }
}
