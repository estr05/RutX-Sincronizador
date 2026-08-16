namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/route-monitor (contrato v2 §6.1):
/// estado por ruta para el mapa en tiempo real.
/// </summary>
public class RouteMonitorListResponse
{
    public List<RouteMonitorItemDto> Data { get; set; } = new();
}

/// <summary>
/// Ítem del monitor: posición, última venta, inicio de jornada y estado.
/// latitude/longitude pueden ser null si la ruta no reporta ubicación aún.
/// </summary>
public class RouteMonitorItemDto
{
    public int RouteId { get; set; }
    public string RouteName { get; set; } = string.Empty;
    public string? Seller { get; set; }
    public LastSaleDto? LastSale { get; set; }
    public DateTime? WorkdayStartedAt { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// Última venta de la ruta: momento en que ocurrió (la pantalla muestra la hora).
/// </summary>
public class LastSaleDto
{
    public DateTime At { get; set; }
}
