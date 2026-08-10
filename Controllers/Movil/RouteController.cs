using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Controllers.Movil;

/// <summary>
/// Controlador para la gestion de Ventas en Ruta.
///
/// ETAPAS:
///   ETAPA 1 - Sincronizacion matutina (GET /api/v1/routes/sync/{vendedorId})
///   ETAPA 3 - Resumen diario (GET /api/v1/routes/summary)
///   ETAPA 3 - Cierre de ruta (POST /api/v1/routes/close)
///
/// La ETAPA 2 (registro de ventas individuales) se maneja en VentasPvController.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/routes")]
public class RouteController : ControllerBase
{
    private readonly IRouteService _routeService;
    private readonly IDebugRouteService _debugRouteService;
    private readonly ILogger<RouteController> _logger;

    public RouteController(
        IRouteService routeService,
        IDebugRouteService debugRouteService,
        ILogger<RouteController> logger)
    {
        _routeService = routeService ?? throw new ArgumentNullException(nameof(routeService));
        _debugRouteService = debugRouteService ?? throw new ArgumentNullException(nameof(debugRouteService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// GET /api/v1/routes/sync
    /// ETAPA 1: Sincronizacion matutina.
    /// El vendedor descarga: su informacion, clientes asignados, productos activos,
    /// caja disponible, folios y formas de cobro.
    ///
    /// La identidad (vendedor, caja, almacen, sucursal) se resuelve del token JWT
    /// (generado en el login con credenciales nativas de Microsip), no de la URL.
    ///
    /// EJEMPLO: GET /api/v1/routes/sync  (con Bearer token)
    /// </summary>
    [HttpGet("sync")]
    public async Task<IActionResult> GetSyncMorning()
    {
        try
        {
            var sesion = ObtenerSesionDeClaims();
            if (sesion == null)
                return Unauthorized(new { mensaje = "Token inválido: faltan datos de sesión." });

            var response = await _routeService.ObtenerSyncMatutinoAsync(sesion);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en sincronizacion matutina");
            return StatusCode(500, new
            {
                mensaje = "Error al conectar con la base de datos.",
                detalle = ex.Message
            });
        }
    }

    /// <summary>
    /// Reconstruye la identidad del usuario desde los claims del token JWT.
    /// Returns null si falta informacion critica.
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
    /// GET /api/v1/routes/summary?fecha={fecha}
    /// ETAPA 3 (pre-cierre): Obtiene el resumen de ventas del dia del usuario.
    /// La identidad (usuario nativo / caja / cajero) se resuelve del token JWT.
    ///
    /// EJEMPLO: GET /api/v1/routes/summary?fecha=2026-07-21  (con Bearer token)
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetDailySummary(
        [FromQuery] DateTime? fecha)
    {
        try
        {
            var sesion = ObtenerSesionDeClaims();
            if (sesion == null)
                return Unauthorized(new { mensaje = "Token inválido: faltan datos de sesión." });

            var fechaConsulta = fecha ?? DateTime.Today;
            var summary = await _routeService.ObtenerResumenDiarioAsync(sesion, fechaConsulta);

            return Ok(new
            {
                mensaje = "Resumen diario obtenido exitosamente",
                vendedor_id = sesion.VendedorId,
                fecha = fechaConsulta.ToString("yyyy-MM-dd"),
                resumen = summary
            });
        }
        catch (Exception ex)
        {
            var errFecha = (fecha ?? DateTime.Today).ToString("yyyy-MM-dd");
            _logger.LogError(ex, "Error al obtener resumen diario");
            return StatusCode(500, new
            {
                mensaje = "Error al obtener el resumen diario.",
                detalle = ex.Message
            });
        }
    }

    /// <summary>
    /// GET /api/v1/routes/debug/doctos-pv?vendedorId={vendedorId}&fecha={fecha}
    /// DEBUG: Lista todos los DOCTOS_PV del vendedor para un dia dado.
    /// Para diagnostico solamente.
    /// </summary>
    [HttpGet("debug/doctos-pv")]
    public async Task<IActionResult> DebugDoctosPv(
        [FromQuery] int vendedorId,
        [FromQuery] DateTime? fecha)
    {
        try
        {
            var fechaConsulta = fecha ?? DateTime.Today;
            var records = await _debugRouteService.ObtenerDebugDoctosPvAsync(vendedorId, fechaConsulta);
            return Ok(new
            {
                vendedor_id = vendedorId,
                fecha = fechaConsulta.ToString("yyyy-MM-dd"),
                total_registros = records.Count(),
                registros = records
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en debug/doctos-pv GET vendedor={VendedorId}", vendedorId);
            return StatusCode(500, new { mensaje = "Error al consultar registros de depuración.", detalle = ex.Message });
        }
    }

    [HttpDelete("debug/doctos-pv")]
    public async Task<IActionResult> DebugDeleteDoctosPv(
        [FromQuery] int vendedorId,
        [FromQuery] DateTime? fecha)
    {
        try
        {
            var fechaConsulta = fecha ?? DateTime.Today;
            var deleted = await _debugRouteService.EliminarDebugDoctosPvAsync(vendedorId, fechaConsulta);
            return Ok(new
            {
                mensaje = $"Se eliminaron {deleted} registros de DOCTOS_PV",
                vendedor_id = vendedorId,
                fecha = fechaConsulta.ToString("yyyy-MM-dd"),
                eliminados = deleted
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en debug/doctos-pv DELETE vendedor={VendedorId}", vendedorId);
            return StatusCode(500, new { mensaje = "Error al eliminar registros de depuración.", detalle = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/v1/routes/close
    /// ETAPA 3: Cierre de ruta al final del dia.
    /// Valida las ventas realizadas vs lo reportado por el vendedor,
    /// calcula diferencias (sobrantes/faltantes) y genera el reporte.
    /// La identidad (usuario nativo / caja / cajero) se resuelve del token JWT.
    /// </summary>
    [HttpPost("close")]
    public async Task<IActionResult> CloseRoute([FromBody] RouteCloseRequestDto request)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var sesion = ObtenerSesionDeClaims();
            if (sesion == null)
                return Unauthorized(new { mensaje = "Token inválido: faltan datos de sesión." });

            var response = await _routeService.CerrarRutaAsync(sesion, request);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Cierre de ruta inválido: {Message}", ex.Message);
            return BadRequest(new { mensaje = "Solicitud inválida.", detalle = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en cierre de ruta");
            return StatusCode(500, new
            {
                mensaje = "Error al procesar el cierre de ruta.",
                detalle = ex.Message
            });
        }
    }
}
