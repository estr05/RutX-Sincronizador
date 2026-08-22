using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Contrato de consultas del tablero web (contrato v2 §6.1).
/// Flujo obligatorio: Controller Web → Interface Web → Service Web → consulta parametrizada.
/// </summary>
public interface IDashboardWebService
{
    /// <summary>GET /api/v2/web/dashboard → DashboardSummaryResponse (7 KPIs + meta).</summary>
    Task<DashboardSummaryResponse> ObtenerResumenAsync(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        CancellationToken ct = default);

    /// <summary>GET /api/v2/web/dashboard/sales-series → SalesSeriesResponse.</summary>
    Task<SalesSeriesResponse> ObtenerSerieVentasAsync(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        CancellationToken ct = default);

    (System.Collections.Generic.List<string> Condiciones, Dapper.DynamicParameters Valores) ConstruirFiltroComun(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        (DateTime Desde, DateTime Hasta) ventana,
        bool incluirFormasCredito);
}
