namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Filtros comunes de los reportes web (contrato v2 §8: ReportFilterQuery).
/// Los campos se envían como query string en snake_case; el model binding
/// de ASP.NET los mapea a estas propiedades PascalCase.
/// </summary>
public class ReportFilterQuery
{
    /// <summary>diario | semanal | mensual</summary>
    public string? Range { get; set; }

    public string? DateFrom { get; set; }

    public string? DateTo { get; set; }

    public int? ZoneId { get; set; }

    public int? RouteId { get; set; }
}
