using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Contrato de reportes web (contrato v2 §6.3).
/// Flujo obligatorio: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
public interface IReportsWebService
{
    /// <summary>GET /api/v2/web/reports/sales → SalesReportResponse (agregados por ruta).</summary>
    Task<SalesReportResponse> ObtenerReporteVentasAsync(ReportFilterQuery filtros, CancellationToken ct = default);

    /// <summary>GET /api/v2/web/reports/sales-comparison → ComparisonResponse (dos series).</summary>
    Task<ComparisonResponse> ObtenerComparativaAsync(ReportFilterQuery filtros, CancellationToken ct = default);

    /// <summary>
    /// GET /api/v2/web/reports/route-profitability → rentabilidad por ruta.
    /// Prioridad Posterior en el contrato v2 §6.3; solo se activa si el cliente
    /// lo contrata (equivalente funcional a VeMobile).
    /// </summary>
    Task<SalesReportResponse> ObtenerRentabilidadPorRutaAsync(ReportFilterQuery filtros, CancellationToken ct = default);
}
