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
    private readonly string _connectionString;

    public ReportsWebService(IConfiguration configuration, ILogger<ReportsWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");
    }

    public async Task<SalesReportResponse> ObtenerReporteVentasAsync(ReportFilterQuery filtros, IReadOnlyList<int> userZoneIds, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        var ventana = VentaQueryConstants.ResolverVentanaResumen(filtros);
        var formasCredito = VentaQueryConstants.LeerFormasCredito(_configuration);

        var (condicionesExtra, valores) = ReportFilterSqlBuilder.Construir(
            filtros,
            userZoneIds,
            ventana,
            incluirFormasCredito: true,
            formasCredito
        );

        var where = string.Join(" AND ", condicionesExtra);
        var condVenta = VentaQueryConstants.CondicionVenta;
        var routeExpr = "COALESCE(NULLIF(TRIM(v.NOMBRE), ''), 'Venta de Mostrador')";

        var sql = $"""
            SELECT
                {routeExpr} AS RouteName,
                CAST(COUNT(CASE WHEN {condVenta} THEN 1 END) AS INTEGER) AS Pieces,
                COALESCE(SUM(CASE WHEN {condVenta}
                    AND NOT EXISTS (SELECT 1 FROM DOCTOS_PV_COBROS cb
                        WHERE cb.DOCTO_PV_ID = pv.DOCTO_PV_ID
                          AND cb.FORMA_COBRO_ID IN @formasCredito)
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS CashAmount,
                COALESCE(SUM(CASE WHEN {condVenta}
                    AND EXISTS (SELECT 1 FROM DOCTOS_PV_COBROS cb
                        WHERE cb.DOCTO_PV_ID = pv.DOCTO_PV_ID
                          AND cb.FORMA_COBRO_ID IN @formasCredito)
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS CreditAmount,
                COALESCE(SUM(CASE WHEN {condVenta}
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS TotalAmount
            FROM DOCTOS_PV pv
            LEFT JOIN VENDEDORES v ON v.VENDEDOR_ID = pv.VENDEDOR_ID
            WHERE {where}
            GROUP BY {routeExpr}
            ORDER BY TotalAmount DESC
            """;

        try
        {
            await using var conn = new FirebirdSql.Data.FirebirdClient.FbConnection(_connectionString);
            var filas = (await Dapper.SqlMapper.QueryAsync<RouteAggregateRow>(conn, 
                new Dapper.CommandDefinition(sql, valores, cancellationToken: ct))).ToList();

            var byRoute = filas.ConvertAll(f => new RouteSalesAggregateDto
            {
                RouteName  = f.RouteName,
                Pieces     = f.Pieces,
                CashAmount = f.CashAmount,
                CreditAmount = f.CreditAmount,
                TotalAmount  = f.TotalAmount,
            });

            return new SalesReportResponse
            {
                ByRoute = byRoute,
                Totals  = CalcularTotales(byRoute),
                Status = "ok",
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error al consultar reporte de ventas por ruta (desde={Desde}, hasta={Hasta})",
                ventana.Desde, ventana.Hasta);
            throw;
        }
    }

    private sealed class RouteAggregateRow
    {
        public string RouteName { get; set; } = string.Empty;
        public int Pieces { get; set; }
        public decimal CashAmount { get; set; }
        public decimal CreditAmount { get; set; }
        public decimal TotalAmount { get; set; }
    }

    /// <summary>
    /// Totales del período calculados EXCLUSIVAMENTE desde las filas by_route,
    /// para que footer, gráfica y KPI usen la misma fuente. Internal para poder
    /// probar la regla sin Firebird.
    /// </summary>
    internal static SalesTotalsDto CalcularTotales(IReadOnlyCollection<RouteSalesAggregateDto> byRoute)
    {
        return new SalesTotalsDto
        {
            SalesAmount  = byRoute.Sum(r => r.TotalAmount),
            CashAmount   = byRoute.Sum(r => r.CashAmount),
            CreditAmount = byRoute.Sum(r => r.CreditAmount),
            TotalAmount  = byRoute.Sum(r => r.TotalAmount),
            Pieces       = byRoute.Sum(r => r.Pieces),
            Currency     = "MXN",
        };
    }

    public Task<ComparisonResponse> ObtenerComparativaAsync(ReportFilterQuery filtros, IReadOnlyList<int> userZoneIds, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        // TODO(web): resolver ventanas de fechas y devolver las dos series
        // comparables (periodo actual vs. mismo periodo del año anterior).
        _logger.LogInformation("Comparativa solicitada (range={Range})", filtros.Range);

        return Task.FromResult(new ComparisonResponse { Currency = "MXN", Status = "unknown" });
    }

    public Task<SalesReportResponse> ObtenerRentabilidadPorRutaAsync(ReportFilterQuery filtros, IReadOnlyList<int> userZoneIds, CancellationToken ct = default)
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
