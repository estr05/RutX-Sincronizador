using Microsoft.AspNetCore.Mvc;

namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Filtros comunes de los reportes web (contrato v2 §8: ReportFilterQuery).
/// Los campos llegan como query string en snake_case; el binding se ancla
/// explícitamente con [FromQuery(Name=...)] porque ASP.NET solo compara
/// nombres sin transformación (date_from ≠ DateFrom).
/// </summary>
public class ReportFilterQuery
{
    /// <summary>diario | semanal | mensual</summary>
    [FromQuery(Name = "range")]
    public string? Range { get; set; }

    [FromQuery(Name = "date_from")]
    public string? DateFrom { get; set; }

    [FromQuery(Name = "date_to")]
    public string? DateTo { get; set; }

    [FromQuery(Name = "zone_id")]
    public int? ZoneId { get; set; }

    [FromQuery(Name = "route_id")]
    public int? RouteId { get; set; }
}
