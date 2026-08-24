using System.Globalization;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Consultas reales del tablero web sobre DOCTOS_PV de Microsip/Firebird
/// (Dapper parametrizado, sin concatenación). Semántica de negocio validada
/// contra RouteService.ObtenerResumenDiarioAsync:
///   - Venta      : TIPO_DOCTO 'V', ESTATUS 'N', excluyendo DESCRIPCION 'NO VENTA:%'.
///   - No venta   : mismo documento con el prefijo 'NO VENTA:' (se registra como 'V').
///   - Cobranza   : TIPO_DOCTO 'P'.
///   - Crédito    : venta con cobro en FORMAS_COBRO configuradas como crédito.
/// Total por documento = IMPORTE_NETO + TOTAL_IMPUESTOS.
///
/// Entrega y Gastos no tienen aún fuente de datos en Microsip: se devuelven en 0
/// con status "unknown" (regla del sprint: cero datos ficticios).
///
/// Autorización de zona (§12.2): zone_ids vacíos = alcance total; una zona pedida
/// fuera del alcance produce un conjunto vacío (intersección silenciosa).
/// </summary>
public class DashboardWebService : IDashboardWebService
{


    private readonly IConfiguration _configuration;
    private readonly ILogger<DashboardWebService> _logger;

    public DashboardWebService(IConfiguration configuration, ILogger<DashboardWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>GET /api/v2/web/dashboard → DashboardSummaryResponse (7 KPIs + meta).</summary>
    public async Task<DashboardSummaryResponse> ObtenerResumenAsync(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Dashboard"))
            throw new FeatureNotReadyException("Dashboard");

        var ventana = ResolverVentanaResumen(filtros);
        var (condiciones, valores) = ConstruirFiltroComun(filtros, userZoneIds, ventana, incluirFormasCredito: true);
        var where = string.Join(" AND ", condiciones);

        var sql = $"""
            SELECT
                COALESCE(SUM(CASE WHEN {VentaQueryConstants.CondicionVenta}
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS VentaTotal,
                COALESCE(SUM(CASE WHEN {VentaQueryConstants.CondicionVenta}
                    AND EXISTS (SELECT 1 FROM DOCTOS_PV_COBROS cb
                                WHERE cb.DOCTO_PV_ID = pv.DOCTO_PV_ID
                                  AND cb.FORMA_COBRO_ID IN @formasCredito)
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS VentaCredito,
                COALESCE(SUM(CASE WHEN pv.TIPO_DOCTO = 'P' AND pv.ESTATUS = 'N'
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Cobranza,
                COUNT(CASE WHEN {VentaQueryConstants.CondicionNoVenta} THEN 1 END) AS NoVentas
            FROM DOCTOS_PV pv
            WHERE {where}
            """;

        try
        {
            await using var conn = new FbConnection(ResolveConnectionString());
            var fila = await conn.QuerySingleAsync<KpiRow>(
                new CommandDefinition(sql, valores, cancellationToken: ct));

            return new DashboardSummaryResponse
            {
                Kpi =
                {
                    new() { Label = "Venta total", Value = fila.VentaTotal, Delta = null, Status = "ok" },
                    new() { Label = "Contado", Value = fila.VentaTotal - fila.VentaCredito, Delta = null, Status = "ok" },
                    new() { Label = "Crédito", Value = fila.VentaCredito, Delta = null, Status = "ok" },
                    new() { Label = "Cobranza", Value = fila.Cobranza, Delta = null, Status = "ok" },
                    new() { Label = "No ventas", Value = fila.NoVentas, Delta = null, Status = "ok" },
                    new() { Label = "Entrega", Value = 0m, Delta = null, Status = "unknown" },
                    new() { Label = "Gastos", Value = 0m, Delta = null, Status = "unknown" },
                },
                Meta = new DashboardMetaDto { LastSyncAt = DateTime.UtcNow, Currency = "MXN" },
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar KPIs del dashboard (desde={Desde}, hasta={Hasta})", ventana.Desde, ventana.Hasta);
            throw;
        }
    }

    /// <summary>GET /api/v2/web/dashboard/sales-series → SalesSeriesResponse.</summary>
    public async Task<SalesSeriesResponse> ObtenerSerieVentasAsync(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        CancellationToken ct = default)
    {
        if (!_configuration.GetValue<bool>("WebFeatures:Dashboard"))
            throw new FeatureNotReadyException("Dashboard");

        var rango = NormalizarRango(filtros.Range);
        var ventana = ResolverVentanaSerie(filtros, rango);
        var (condiciones, valores) = ConstruirFiltroComun(filtros, userZoneIds, ventana, incluirFormasCredito: false);
        var where = string.Join(" AND ", condiciones);

        var sql = rango switch
        {
            "mensual" => $"""
               SELECT CAST(EXTRACT(YEAR FROM CAST(pv.FECHA AS DATE)) AS INTEGER) AS Anio,
                      CAST(EXTRACT(MONTH FROM CAST(pv.FECHA AS DATE)) AS INTEGER) AS Mes,
                      COALESCE(SUM(CASE WHEN {VentaQueryConstants.CondicionVenta}
                          THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Monto
               FROM DOCTOS_PV pv
               WHERE {where}
               GROUP BY 1, 2
               ORDER BY 1, 2
               """,
            "diario" => $"""
               SELECT CAST(pv.FECHA AS DATE) AS Dia,
                      CAST(EXTRACT(HOUR FROM pv.HORA) AS INTEGER) AS Hora,
                      COALESCE(SUM(CASE WHEN {VentaQueryConstants.CondicionVenta}
                          THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Monto
               FROM DOCTOS_PV pv
               WHERE {where}
               GROUP BY 1, 2
               ORDER BY 1, 2
               """,
            "semanal" => $"""
               SELECT CAST(pv.FECHA AS DATE) AS Dia,
                      COALESCE(SUM(CASE WHEN {VentaQueryConstants.CondicionVenta}
                          THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Monto
               FROM DOCTOS_PV pv
               WHERE {where}
               GROUP BY 1
               ORDER BY 1
               """,
            _ => throw new InvalidOperationException($"Rango '{rango}' no manejado.")
        };

        try
        {
            await using var conn = new FbConnection(ResolveConnectionString());
            var response = new SalesSeriesResponse { Currency = "MXN", Status = "ok" };

            if (rango == "mensual")
            {
                var filas = await conn.QueryAsync<SerieMensualRow>(
                    new CommandDefinition(sql, valores, cancellationToken: ct));
                var dict = filas.ToDictionary(f => $"{f.Anio:D4}-{f.Mes:D2}", f => f.Monto);
                var actual = ventana.Desde.Date;
                var fin = ventana.Hasta.Date;
                while (actual <= fin)
                {
                    var p = $"{actual:yyyy-MM}";
                    if (!response.Series.Any(s => s.Period == p))
                        response.Series.Add(new SalesPointDto { Period = p, Amount = dict.GetValueOrDefault(p, 0m) });
                    actual = actual.AddMonths(1);
                }
            }
            else if (rango == "diario")
            {
                var filas = await conn.QueryAsync<SeriePorHoraRow>(
                    new CommandDefinition(sql, valores, cancellationToken: ct));
                var dict = filas.ToDictionary(f => $"{f.Dia:yyyy-MM-dd} {f.Hora:D2}:00", f => f.Monto);
                var actual = ventana.Desde.Date;
                var fin = ventana.Hasta.Date;
                while (actual <= fin)
                {
                    for (int h = 0; h < 24; h++)
                    {
                        var p = $"{actual:yyyy-MM-dd} {h:D2}:00";
                        response.Series.Add(new SalesPointDto { Period = p, Amount = dict.GetValueOrDefault(p, 0m) });
                    }
                    actual = actual.AddDays(1);
                }
            }
            else if (rango == "semanal")
            {
                var filas = await conn.QueryAsync<SerieRow>(
                    new CommandDefinition(sql, valores, cancellationToken: ct));
                var dict = filas.ToDictionary(f => f.Dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f => f.Monto);
                var actual = ventana.Desde.Date;
                var fin = ventana.Hasta.Date;
                while (actual <= fin)
                {
                    var p = actual.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    response.Series.Add(new SalesPointDto { Period = p, Amount = dict.GetValueOrDefault(p, 0m) });
                    actual = actual.AddDays(1);
                }
            }
            else
            {
                // Fallback inalcanzable (el switch arriba tira excepción si escapa a los 3 conocidos)
                throw new InvalidOperationException($"Rango '{rango}' no soportado en materialización.");
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar serie de ventas (rango={Rango}, desde={Desde}, hasta={Hasta})", rango, ventana.Desde, ventana.Hasta);
            throw;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // CONSTRUCCIÓN DE FILTROS COMUNES (fechas, ruta, zonas, formas de crédito)
    // ────────────────────────────────────────────────────────────────────────

    internal (List<string> Condiciones, DynamicParameters Valores) ConstruirFiltroComun(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        (DateTime Desde, DateTime Hasta) ventana,
        bool incluirFormasCredito)
    {
        return ReportFilterSqlBuilder.Construir(
            filtros,
            userZoneIds,
            ventana,
            incluirFormasCredito,
            incluirFormasCredito ? LeerFormasCredito() : Array.Empty<int>());
    }

    /// <summary>Formas de cobro que el cliente considera crédito; sin config, ninguna (-1).</summary>
    internal int[] LeerFormasCredito() => VentaQueryConstants.LeerFormasCredito(_configuration);

    private string ResolveConnectionString()
        => _configuration.GetConnectionString("FirebirdConnection")
           ?? throw new InvalidOperationException("FirebirdConnection no configurada.");

    // =========================================================================
    // WRAPPERS DE COMPATIBILIDAD
    // Delegan a VentaQueryConstants para no romper los tests existentes que 
    // actualmente llaman a estos métodos desde DashboardWebService.
    // =========================================================================

    internal static string NormalizarRango(string? rango) => VentaQueryConstants.NormalizarRango(rango);

    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaResumen(ReportFilterQuery filtros) 
        => VentaQueryConstants.ResolverVentanaResumen(filtros);

    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaSerie(ReportFilterQuery filtros, string rango) 
        => VentaQueryConstants.ResolverVentanaSerie(filtros, rango);

    internal static DateTime? ParsearFecha(string? fecha) => VentaQueryConstants.ParsearFecha(fecha);

    internal static DateTime LunesDe(DateTime fecha) => VentaQueryConstants.LunesDe(fecha);

    /// <summary>Fila del agregado de KPIs.</summary>
    private sealed class KpiRow
    {
        public decimal VentaTotal { get; set; }
        public decimal VentaCredito { get; set; }
        public decimal Cobranza { get; set; }
        public int NoVentas { get; set; }
    }

    /// <summary>Fila diaria/mensual de la serie de ventas.</summary>
    private sealed class SerieRow
    {
        public DateTime Dia { get; set; }
        public decimal Monto { get; set; }
    }

    private sealed class SeriePorHoraRow
    {
        public DateTime Dia { get; set; }
        public int Hora { get; set; }
        public decimal Monto { get; set; }
    }

    /// <summary>Fila mensual de la serie de ventas.</summary>
    private sealed class SerieMensualRow
    {
        public int Anio { get; set; }
        public int Mes { get; set; }
        public decimal Monto { get; set; }
    }
}
