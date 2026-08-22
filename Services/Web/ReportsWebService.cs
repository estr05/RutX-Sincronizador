using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Reportes web sobre Firebird/Microsip (consultas parametrizadas).
/// El esqueleto responde la forma exacta del contrato v2 con valores cero
/// hasta que se conecten las consultas de negocio reales.
/// </summary>
public class ReportsWebService : IReportsWebService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReportsWebService> _logger;
    private readonly IDashboardWebService _dashboardService;
    private readonly string _connectionString;

    public ReportsWebService(
        IConfiguration configuration,
        ILogger<ReportsWebService> logger,
        IDashboardWebService dashboardService)
    {
        _configuration = configuration;
        _logger = logger;
        _dashboardService = dashboardService;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");
    }

    public async Task<SalesReportResponse> ObtenerReporteVentasAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        var ventana = DashboardWebService.ResolverVentanaResumen(filtros);
        var (condiciones, valores) = _dashboardService.ConstruirFiltroComun(filtros, Array.Empty<int>(), ventana, incluirFormasCredito: true);
        var where = string.Join(" AND ", condiciones);

        var sql = $"""
            SELECT
                COALESCE(v.NOMBRE, 'Sin asignar') AS RouteName,
                COALESCE(SUM((SELECT SUM(det.UNIDADES) FROM DOCTOS_PV_DET det WHERE det.DOCTO_PV_ID = pv.DOCTO_PV_ID)), 0) AS Pieces,
                COALESCE(SUM(CASE WHEN EXISTS (
                    SELECT 1 FROM DOCTOS_PV_COBROS cb WHERE cb.DOCTO_PV_ID = pv.DOCTO_PV_ID AND cb.FORMA_COBRO_ID IN @formasCredito
                ) THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS ELSE 0 END), 0) AS CreditAmount,
                COALESCE(SUM(pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS), 0) AS TotalAmount
            FROM DOCTOS_PV pv
            LEFT JOIN VENDEDORES v ON v.VENDEDOR_ID = pv.VENDEDOR_ID
            WHERE {where} AND {DashboardWebService.CondicionVenta}
            GROUP BY v.VENDEDOR_ID, v.NOMBRE
            """;

        _logger.LogInformation("Reporte de ventas solicitado (range={Range})", filtros.Range);

        try
        {
            await using var conn = new FirebirdSql.Data.FirebirdClient.FbConnection(_connectionString);
            var filas = await Dapper.SqlMapper.QueryAsync<RouteSalesAggregateDto>(
                conn, new Dapper.CommandDefinition(sql, valores, cancellationToken: ct));

            var listaRutas = filas.ToList();

            foreach (var ruta in listaRutas)
            {
                ruta.CashAmount = ruta.TotalAmount - ruta.CreditAmount;
            }

            var response = new SalesReportResponse
            {
                Totals = new SalesTotalsDto
                {
                    Currency = "MXN",
                    Pieces = listaRutas.Sum(r => r.Pieces),
                    SalesAmount = listaRutas.Sum(r => r.TotalAmount)
                },
                ByRoute = listaRutas,
                Status = "ok",
            };

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar reporte de ventas por ruta (desde={Desde}, hasta={Hasta})", ventana.Desde, ventana.Hasta);
            throw;
        }
    }

    public Task<ComparisonResponse> ObtenerComparativaAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        // TODO(web): resolver ventanas de fechas y devolver las dos series
        // comparables (periodo actual vs. mismo periodo del año anterior).
        _logger.LogInformation("Comparativa solicitada (range={Range})", filtros.Range);

        return Task.FromResult(new ComparisonResponse { Currency = "MXN", Status = "unknown" });
    }

    public Task<SalesReportResponse> ObtenerRentabilidadPorRutaAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        // TODO(web): agregados de venta, gasto, entrega y costo disponible por ruta.
        // Endpoint Prioridad Posterior (§6.3): no publicar hasta activación comercial.
        _logger.LogInformation("Rentabilidad por ruta solicitada (range={Range})", filtros.Range);

        return Task.FromResult(new SalesReportResponse
        {
            Totals = new SalesTotalsDto { Currency = "MXN" },
            Status = "unknown",
        });
    }
}
