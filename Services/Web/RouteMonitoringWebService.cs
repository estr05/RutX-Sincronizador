using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Monitoreo de rutas web sobre Firebird/Microsip (consultas parametrizadas).
/// El esqueleto responde la forma exacta del contrato v2 con valores cero
/// hasta que se conecten las consultas de negocio reales.
/// </summary>
public class RouteMonitoringWebService : IRouteMonitoringWebService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RouteMonitoringWebService> _logger;
    private readonly string _connectionString;

    public RouteMonitoringWebService(IConfiguration configuration, ILogger<RouteMonitoringWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");
    }

    public Task<RouteMonitorListResponse> ObtenerMonitoreoAsync(RouteMonitorQuery filtros, CancellationToken ct = default)
    {
        // TODO(web): proyección de jornada, visita y ubicación más reciente por ruta
        // con Dapper parametrizado (filtros por zona/ruta).
        _logger.LogInformation("Monitoreo solicitado (zone={Zone}, route={Route})", filtros.ZoneId, filtros.RouteId);

        return Task.FromResult(new RouteMonitorListResponse());
    }

    public Task<RouteTimelineResponse> ObtenerDetalleRutaAsync(int routeId, CancellationToken ct = default)
    {
        // TODO(web): línea temporal del día, vendedor, duración de visitas y última venta.
        _logger.LogInformation("Detalle de ruta solicitado (routeId={RouteId})", routeId);

        return Task.FromResult(new RouteTimelineResponse
        {
            RouteId = routeId,
            Status = "unknown",
        });
    }

    public Task<List<RouteMonitorItemDto>> ObtenerCatalogoRutasAsync(RouteMonitorQuery filtros, CancellationToken ct = default)
    {
        // TODO(web): catálogo paginado de rutas acotado por permisos de zona.
        _logger.LogInformation("Catálogo de rutas solicitado (zone={Zone})", filtros.ZoneId);

        return Task.FromResult(new List<RouteMonitorItemDto>());
    }
}
