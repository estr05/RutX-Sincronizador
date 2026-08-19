namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/route-monitor/{route_id} (contrato v2 §6.1):
/// línea temporal del día de la ruta, vendedor y última venta.
/// </summary>
public class RouteTimelineResponse
{
    public int RouteId { get; set; }
    public string? Seller { get; set; }
    public LastSaleDto? LastSale { get; set; }
    public List<TimelineEventDto> Timeline { get; set; } = new();
    public string Status { get; set; } = "unknown";
}

/// <summary>
/// Evento de la línea temporal: hora, cliente, tipo de visita
/// (visit = atendida, no-sale = no venta), duración y monto opcional.
/// </summary>
public class TimelineEventDto
{
    public DateTime At { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Type { get; set; } = "no-sale";
    public int? DurationMinutes { get; set; }
    public decimal? Amount { get; set; }
}
