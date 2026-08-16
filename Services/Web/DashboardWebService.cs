using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Consultas del tablero web sobre Firebird/Microsip (consultas parametrizadas,
/// nunca concatenación). El esqueleto responde la forma exacta del contrato v2
/// con valores cero hasta que se conecten las consultas de negocio reales.
/// </summary>
public class DashboardWebService : IDashboardWebService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DashboardWebService> _logger;
    private readonly string _connectionString;

    public DashboardWebService(IConfiguration configuration, ILogger<DashboardWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _connectionString = configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");
    }

    public Task<DashboardSummaryResponse> ObtenerResumenAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        // TODO(web): implementar agregados de ventas, contado, crédito, cobranza,
        // no ventas, entregas y gastos por rango/filtros con Dapper parametrizado.
        _logger.LogInformation("Dashboard resumen solicitado (range={Range}, zone={Zone})", filtros.Range, filtros.ZoneId);

        return Task.FromResult(new DashboardSummaryResponse
        {
            Kpi =
            {
                new() { Label = "Venta total", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "Contado", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "Crédito", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "Cobranza", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "No ventas", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "Entrega", Value = 0m, Delta = null, Status = "unknown" },
                new() { Label = "Gastos", Value = 0m, Delta = null, Status = "unknown" },
            },
            Meta = new DashboardMetaDto { LastSyncAt = null, Currency = "MXN" },
        });
    }

    public Task<SalesSeriesResponse> ObtenerSerieVentasAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        // TODO(web): implementar serie por periodo (diario/semanal/mensual) con Dapper parametrizado.
        _logger.LogInformation("Serie de ventas solicitada (range={Range})", filtros.Range);

        return Task.FromResult(new SalesSeriesResponse { Currency = "MXN", Status = "unknown" });
    }
}
