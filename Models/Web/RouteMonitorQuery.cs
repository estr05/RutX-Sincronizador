namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Filtros del monitoreo de rutas (contrato v2 §8: RouteMonitorQuery).
/// </summary>
public class RouteMonitorQuery
{
    public int? ZoneId { get; set; }

    public int? RouteId { get; set; }
}
