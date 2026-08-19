using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Contrato del monitoreo de rutas web (contrato v2 §6.1).
/// Flujo obligatorio: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
public interface IRouteMonitoringWebService
{
    /// <summary>GET /api/v2/web/route-monitor → RouteMonitorListResponse (mapa en vivo).</summary>
    Task<RouteMonitorListResponse> ObtenerMonitoreoAsync(RouteMonitorQuery filtros, CancellationToken ct = default);

    /// <summary>GET /api/v2/web/route-monitor/{route_id} → RouteTimelineResponse (detalle de ruta).</summary>
    Task<RouteTimelineResponse> ObtenerDetalleRutaAsync(int routeId, CancellationToken ct = default);

    /// <summary>GET /api/v2/web/routes → catálogo paginado de rutas para filtros.</summary>
    Task<List<RouteMonitorItemDto>> ObtenerCatalogoRutasAsync(RouteMonitorQuery filtros, CancellationToken ct = default);
}
