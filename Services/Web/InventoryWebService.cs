using System.Globalization;
using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Inventory;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Implementación del piloto de inventario por ruta (fórmula aprobada §12.2):
///
///   disponible = Σ(ENTRADAS_UNIDADES − SALIDAS_UNIDADES) histórico del ledger
///                SALDOS_IN para el almacén de la ruta
///   vendido    = SALIDAS_UNIDADES del periodo as_of (informativo, no se resta)
///   diferencia = disponible − vendido (informativo; NO es existencia física)
///
/// Almacén de la ruta: ALMACEN_ID más reciente de DOCTOS_PV para el vendedor
/// dentro de la ventana de operación (últimos 90 días). Sin operación reciente
/// → NOT_FOUND (el vendedor no tiene almacén de ruta asignado).
/// Unidad de venta: ARTICULOS.UNIDAD_VENTA (Microsip no tiene columna UNIDAD).
/// </summary>
public sealed class InventoryWebService : IInventoryWebService
{
    private const int VentanaOperacionDias = 90;

    private readonly IConfiguration _configuration;
    private readonly ILogger<InventoryWebService> _logger;

    public InventoryWebService(IConfiguration configuration, ILogger<InventoryWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WebInventoryResult> ListByRouteAsync(
        InventoryRouteQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default)
    {
        var (page, perPage) = NormalizarPaginacion(query.Page, query.PerPage);

        if (query.ZoneId is int zona && userZoneIds.Count > 0 && !userZoneIds.Contains(zona))
            return WebInventoryResult.Error("FORBIDDEN_ZONE", "La zona solicitada no está dentro de las zonas autorizadas de tu usuario.");

        if (query.RouteId is not int ruta)
            return WebInventoryResult.Error("VALIDATION_ERROR", "route_id es requerido para el piloto de inventario por ruta.");

        if (query.AsOf is not null && !EsPeriodoValido(query.AsOf))
            return WebInventoryResult.Error("VALIDATION_ERROR", "as_of debe tener el formato YYYY-MM.");

        var connectionString = _configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");

        try
        {
            await using var conn = new FbConnection(connectionString);

            var almacen = await ResolverAlmacenDeRutaAsync(conn, ruta, cancellationToken);
            if (almacen is null)
                return WebInventoryResult.Error("NOT_FOUND", "La ruta no tiene un almacén de operación reciente (últimos 90 días).");

            var (ano, mes) = ResolverPeriodo(query.AsOf, conn, almacen.Value, cancellationToken);

            var filas = await conn.QueryAsync<SaldoFila>(
                new CommandDefinition(
                    """
                    SELECT s.ARTICULO_ID, a.NOMBRE AS NOMBRE, COALESCE(a.UNIDAD_VENTA, '') AS UNIDAD,
                           COALESCE(SUM(s.ENTRADAS_UNIDADES), 0) AS ENTRADAS,
                           COALESCE(SUM(s.SALIDAS_UNIDADES), 0) AS SALIDAS
                    FROM SALDOS_IN s
                    LEFT JOIN ARTICULOS a ON a.ARTICULO_ID = s.ARTICULO_ID
                    WHERE s.ALMACEN_ID = @almacen
                      AND (s.ANO < @ano OR (s.ANO = @ano AND s.MES <= @mes))
                    GROUP BY s.ARTICULO_ID, a.NOMBRE, a.UNIDAD_VENTA
                    HAVING COALESCE(SUM(s.ENTRADAS_UNIDADES), 0) - COALESCE(SUM(s.SALIDAS_UNIDADES), 0) <> 0
                    ORDER BY a.NOMBRE ASC ROWS @inicio TO @fin
                    """,
                    new { almacen = almacen.Value, ano, mes, inicio = (page - 1) * perPage + 1, fin = page * perPage },
                    cancellationToken: cancellationToken));

            var vendidoPorArticulo = await ResolverVendidoDelPeriodoAsync(
                conn, almacen.Value, ano, mes, cancellationToken);

            var total = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    SELECT COUNT(*) FROM (
                        SELECT s.ARTICULO_ID
                        FROM SALDOS_IN s
                        LEFT JOIN ARTICULOS a ON a.ARTICULO_ID = s.ARTICULO_ID
                        WHERE s.ALMACEN_ID = @almacen
                          AND (s.ANO < @ano OR (s.ANO = @ano AND s.MES <= @mes))
                        GROUP BY s.ARTICULO_ID, a.NOMBRE, a.UNIDAD_VENTA
                        HAVING COALESCE(SUM(s.ENTRADAS_UNIDADES), 0) - COALESCE(SUM(s.SALIDAS_UNIDADES), 0) <> 0
                    ) t
                    """,
                    new { almacen = almacen.Value, ano, mes },
                    cancellationToken: cancellationToken));

            var items = filas.Select(f =>
            {
                var disponible = f.Entradas - f.Salidas;
                var vendido = vendidoPorArticulo.GetValueOrDefault(f.ArticuloId);
                return new InventoryRouteRowDto(
                    f.ArticuloId,
                    f.ArticuloId.ToString(),
                    f.Nombre ?? f.ArticuloId.ToString(),
                    f.Unidad,
                    disponible,
                    vendido,
                    disponible - vendido);
            }).ToList();

            return WebInventoryResult.Exito(new WebListResponse<InventoryRouteRowDto>(
                items,
                WebPageMeta.Create(page, perPage, total),
                new { page, per_page = perPage, zone_id = query.ZoneId, route_id = query.RouteId, as_of = query.AsOf ?? $"{ano:0000}-{mes:00}", almacen = almacen.Value }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar inventario de ruta {RouteId}", ruta);
            return WebInventoryResult.Error("API_UNAVAILABLE", "No se pudo consultar el inventario de la ruta.");
        }
    }

    /// <summary>
    /// Almacén de la ruta: ALMACEN_ID más reciente en DOCTOS_PV para el vendedor.
    /// </summary>
    private static async Task<int?> ResolverAlmacenDeRutaAsync(FbConnection conn, int ruta, CancellationToken cancellationToken)
    {
        var fila = await conn.QueryFirstOrDefaultAsync<(int AlmacenId, long DoctoPvId)?>(
            new CommandDefinition(
                """
                SELECT FIRST 1 d.ALMACEN_ID, d.DOCTO_PV_ID
                FROM DOCTOS_PV d
                WHERE d.VENDEDOR_ID = @ruta
                  AND d.ALMACEN_ID IS NOT NULL
                  AND d.FECHA >= DATEADD(-90 DAY TO CURRENT_DATE)
                ORDER BY d.FECHA DESC, d.DOCTO_PV_ID DESC
                """,
                new { ruta },
                cancellationToken: cancellationToken));

        return fila?.AlmacenId;
    }

    /// <summary>
    /// Periodo as_of: por defecto el último periodo con ledger del almacén.
    /// </summary>
    private static (int Ano, int Mes) ResolverPeriodo(
        string? asOf,
        FbConnection conn,
        int almacen,
        CancellationToken cancellationToken)
    {
        if (asOf is not null)
        {
            var partes = asOf.Split('-');
            return (int.Parse(partes[0]), int.Parse(partes[1]));
        }

        var ultimo = conn.QueryFirstOrDefaultAsync<(int Ano, int Mes)?>(
            new CommandDefinition(
                """
                SELECT FIRST 1 s.ANO, s.MES
                FROM SALDOS_IN s
                WHERE s.ALMACEN_ID = @almacen
                ORDER BY s.ANO DESC, s.MES DESC
                """,
                new { almacen },
                cancellationToken: cancellationToken)).GetAwaiter().GetResult();

        return ultimo ?? (DateTime.Today.Year, DateTime.Today.Month);
    }

    private static async Task<IReadOnlyDictionary<int, decimal>> ResolverVendidoDelPeriodoAsync(
        FbConnection conn,
        int almacen,
        int ano,
        int mes,
        CancellationToken cancellationToken)
    {
        var filas = await conn.QueryAsync<VentaPeriodoFila>(
            new CommandDefinition(
                """
                SELECT s.ARTICULO_ID, COALESCE(SUM(s.SALIDAS_UNIDADES), 0) AS VENDIDO
                FROM SALDOS_IN s
                WHERE s.ALMACEN_ID = @almacen AND s.ANO = @ano AND s.MES = @mes
                GROUP BY s.ARTICULO_ID
                """,
                new { almacen, ano, mes },
                cancellationToken: cancellationToken));

        return filas.ToDictionary(f => f.ArticuloId, f => f.Vendido);
    }

    private static bool EsPeriodoValido(string asOf)
        => asOf.Length == 7
           && asOf[4] == '-'
           && int.TryParse(asOf.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var ano)
           && ano >= 2000
           && int.TryParse(asOf.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mes)
           && mes is >= 1 and <= 12;

    private static (int Page, int PerPage) NormalizarPaginacion(int page, int perPage)
    {
        var p = Math.Clamp(page, InventoryRouteQuery.MinPage, int.MaxValue);
        var pp = Math.Clamp(perPage, InventoryRouteQuery.MinPerPage, InventoryRouteQuery.MaxPerPage);
        return (p, pp);
    }

    private sealed record SaldoFila(
        int ArticuloId,
        string? Nombre,
        string Unidad,
        decimal Entradas,
        decimal Salidas);

    private sealed record VentaPeriodoFila(
        int ArticuloId,
        decimal Vendido);
}