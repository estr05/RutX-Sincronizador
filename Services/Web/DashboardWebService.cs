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
    private const string CondicionVenta =
        "pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' AND (pv.DESCRIPCION IS NULL OR pv.DESCRIPCION NOT LIKE 'NO VENTA:%')";

    private const string CondicionNoVenta =
        "pv.TIPO_DOCTO = 'V' AND pv.ESTATUS = 'N' AND pv.DESCRIPCION LIKE 'NO VENTA:%'";

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
                COALESCE(SUM(CASE WHEN {CondicionVenta}
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS VentaTotal,
                COALESCE(SUM(CASE WHEN {CondicionVenta}
                    AND EXISTS (SELECT 1 FROM DOCTOS_PV_COBROS cb
                                WHERE cb.DOCTO_PV_ID = pv.DOCTO_PV_ID
                                  AND cb.FORMA_COBRO_ID IN @formasCredito)
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS VentaCredito,
                COALESCE(SUM(CASE WHEN pv.TIPO_DOCTO = 'P' AND pv.ESTATUS = 'N'
                    THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Cobranza,
                COUNT(CASE WHEN {CondicionNoVenta} THEN 1 END) AS NoVentas
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

        var sql = rango == "mensual"
            ? $"""
               SELECT CAST(EXTRACT(YEAR FROM CAST(pv.FECHA AS DATE)) AS INTEGER) AS Anio,
                      CAST(EXTRACT(MONTH FROM CAST(pv.FECHA AS DATE)) AS INTEGER) AS Mes,
                      COALESCE(SUM(CASE WHEN {CondicionVenta}
                          THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Monto
               FROM DOCTOS_PV pv
               WHERE {where}
               GROUP BY 1, 2
               ORDER BY 1, 2
               """
            : $"""
               SELECT CAST(pv.FECHA AS DATE) AS Dia,
                      COALESCE(SUM(CASE WHEN {CondicionVenta}
                          THEN pv.IMPORTE_NETO + pv.TOTAL_IMPUESTOS END), 0) AS Monto
               FROM DOCTOS_PV pv
               WHERE {where}
               GROUP BY 1
               ORDER BY 1
               """;

        try
        {
            await using var conn = new FbConnection(ResolveConnectionString());
            var response = new SalesSeriesResponse { Currency = "MXN", Status = "ok" };

            if (rango == "mensual")
            {
                var filas = await conn.QueryAsync<SerieMensualRow>(
                    new CommandDefinition(sql, valores, cancellationToken: ct));
                foreach (var f in filas)
                    response.Series.Add(new SalesPointDto
                    {
                        Period = $"{f.Anio:D4}-{f.Mes:D2}",
                        Amount = f.Monto,
                    });
            }
            else
            {
                var filas = await conn.QueryAsync<SerieRow>(
                    new CommandDefinition(sql, valores, cancellationToken: ct));
                foreach (var f in filas)
                    response.Series.Add(new SalesPointDto
                    {
                        Period = f.Dia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        Amount = f.Monto,
                    });
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

    /// <summary>
    /// Condiciones WHERE compartidas por resumen y serie. Las fechas siempre
    /// acotan el rango; zona y ruta son opcionales. La lista de zonas vacía
    /// (zona fuera de alcance) produce un predicado falso: cero resultados.
    /// Internal para poder probar las reglas de negocio sin Firebird.
    /// </summary>
    internal (List<string> Condiciones, DynamicParameters Valores) ConstruirFiltroComun(
        ReportFilterQuery filtros,
        IReadOnlyList<int> userZoneIds,
        (DateTime Desde, DateTime Hasta) ventana,
        bool incluirFormasCredito)
    {
        var condiciones = new List<string>
        {
            "pv.FECHA >= @desde",
            "pv.FECHA <= @hasta",
        };

        var valores = new DynamicParameters();
        valores.Add("@desde", ventana.Desde.Date);
        valores.Add("@hasta", ventana.Hasta.Date);

        if (filtros.RouteId is int ruta)
        {
            condiciones.Add("pv.VENDEDOR_ID = @ruta");
            valores.Add("@ruta", ruta);
        }

        List<int>? zonasActivas;
        if (filtros.ZoneId is int zona)
            zonasActivas = userZoneIds.Count == 0 || userZoneIds.Contains(zona)
                ? new List<int> { zona }
                : new List<int>();
        else if (userZoneIds.Count > 0)
            zonasActivas = userZoneIds.ToList();
        else
            zonasActivas = null;

        if (zonasActivas != null)
        {
            condiciones.Add(
                "EXISTS (SELECT 1 FROM CLIENTES cz WHERE cz.CLIENTE_ID = pv.CLIENTE_ID AND cz.ZONA_CLIENTE_ID IN @zonas)");
            valores.Add("@zonas", zonasActivas);
        }

        if (incluirFormasCredito)
            valores.Add("@formasCredito", LeerFormasCredito());

        return (condiciones, valores);
    }

    /// <summary>Formas de cobro que el cliente considera crédito; sin config, ninguna (-1).</summary>
    internal int[] LeerFormasCredito()
    {
        var formas = _configuration.GetSection("MicrosipSettings:CreditFormaCobroIds").Get<int[]>();
        return formas is { Length: > 0 } ? formas : new[] { -1 };
    }

    private string ResolveConnectionString()
        => _configuration.GetConnectionString("FirebirdConnection")
           ?? throw new InvalidOperationException("FirebirdConnection no configurada.");

    internal static string NormalizarRango(string? rango)
        => rango?.Trim().ToLowerInvariant() is "semanal" or "mensual"
            ? rango.Trim().ToLowerInvariant()
            : "diario";

    /// <summary>Ventana del resumen: por defecto el día en curso según el rango pedido.</summary>
    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaResumen(ReportFilterQuery filtros)
    {
        var explicita = VentanaExplicita(filtros);
        if (explicita.HasValue)
            return explicita.Value;

        var hoy = DateTime.Today;
        return NormalizarRango(filtros.Range) switch
        {
            "semanal" => (LunesDe(hoy), hoy),
            "mensual" => (new DateTime(hoy.Year, hoy.Month, 1), hoy),
            _ => (hoy, hoy),
        };
    }

    /// <summary>Ventana de la serie: 14 días, 8 semanas o 12 meses hacia atrás.</summary>
    internal static (DateTime Desde, DateTime Hasta) ResolverVentanaSerie(ReportFilterQuery filtros, string rango)
    {
        var explicita = VentanaExplicita(filtros);
        if (explicita.HasValue)
            return explicita.Value;

        var hoy = DateTime.Today;
        return rango switch
        {
            "mensual" => (new DateTime(hoy.Year, hoy.Month, 1).AddMonths(-11), hoy),
            "semanal" => (hoy.AddDays(-55), hoy),
            _ => (hoy.AddDays(-13), hoy),
        };
    }

    private static (DateTime, DateTime)? VentanaExplicita(ReportFilterQuery filtros)
    {
        var desde = ParsearFecha(filtros.DateFrom);
        var hasta = ParsearFecha(filtros.DateTo);
        return desde.HasValue && hasta.HasValue ? (desde.Value, hasta.Value) : null;
    }

    internal static DateTime? ParsearFecha(string? fecha)
        => DateTime.TryParseExact(
               fecha,
               "yyyy-MM-dd",
               CultureInfo.InvariantCulture,
               DateTimeStyles.None,
               out var valor)
            ? valor
            : null;

    internal static DateTime LunesDe(DateTime fecha)
    {
        // EXTRACT(WEEKDAY) en Firebird cuenta domingo=0; aquí anclamos a lunes.
        var desplazamiento = ((int)fecha.DayOfWeek + 6) % 7;
        return fecha.AddDays(-desplazamiento);
    }

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

    /// <summary>Fila mensual de la serie de ventas.</summary>
    private sealed class SerieMensualRow
    {
        public int Anio { get; set; }
        public int Mes { get; set; }
        public decimal Monto { get; set; }
    }
}
