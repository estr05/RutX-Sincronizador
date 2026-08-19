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

    public Task<SalesReportResponse> ObtenerReporteVentasAsync(ReportFilterQuery filtros, CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Reports"))
            throw new FeatureNotReadyException("Reports");

        // TODO(web): agregados de ventas, piezas y montos por ruta con Dapper parametrizado.
        _logger.LogInformation("Reporte de ventas solicitado (range={Range})", filtros.Range);

        return Task.FromResult(new SalesReportResponse
        {
            Totals = new SalesTotalsDto { Currency = "MXN" },
            Status = "unknown",
        });
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
