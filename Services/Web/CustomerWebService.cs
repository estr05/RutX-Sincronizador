using Dapper;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Customers;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Implementación de ICustomerWebService: consultas parametrizadas (Dapper)
/// sobre CLIENTES/ZONAS_CLIENTES/VENDEDORES de Microsip.
///
/// Autorización de zona (§12.2): zone_ids del usuario vacíos = alcance total;
/// si el usuario tiene zonas y pide una zona fuera de ellas → FORBIDDEN_ZONE
/// (manipular zone_id en la URL no escapa de las zonas autorizadas).
/// </summary>
public sealed class CustomerWebService : ICustomerWebService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<CustomerWebService> _logger;

    public CustomerWebService(IConfiguration configuration, ILogger<CustomerWebService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WebCustomerResult> ListAsync(
        CustomerListQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default)
    {
        var (page, perPage) = NormalizarPaginacion(query.Page, query.PerPage);

        if (query.ZoneId is int zona && userZoneIds.Count > 0 && !userZoneIds.Contains(zona))
            return WebCustomerResult.Error("FORBIDDEN_ZONE", "La zona solicitada no está dentro de las zonas autorizadas de tu usuario.");

        var connectionString = _configuration.GetConnectionString("FirebirdConnection")
            ?? throw new InvalidOperationException("FirebirdConnection no configurada.");

        var where = new List<string>();
        var parametros = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var busqueda = EscapeLike(query.Search.Trim());
            where.Add("(UPPER(c.NOMBRE) LIKE UPPER($busqueda) OR CAST(c.CLIENTE_ID AS VARCHAR(20)) LIKE $busqueda)");
            parametros.Add("$busqueda", $"%{busqueda}%");
        }

        if (query.ZoneId is int zonaFiltro)
        {
            where.Add("c.ZONA_CLIENTE_ID = $zona");
            parametros.Add("$zona", zonaFiltro);
        }

        if (query.RouteId is int ruta)
        {
            where.Add("c.VENDEDOR_ID = $ruta");
            parametros.Add("$ruta", ruta);
        }

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            where.Add("c.ESTATUS = $estatus");
            parametros.Add("$estatus", query.Status.Trim());
        }

        var clausulaWhere = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        var baseSql = $"""
            SELECT c.CLIENTE_ID, c.NOMBRE, c.ESTATUS, c.ZONA_CLIENTE_ID, z.NOMBRE AS ZONA_NOMBRE,
                   c.VENDEDOR_ID, v.NOMBRE AS VENDEDOR_NOMBRE
            FROM CLIENTES c
            LEFT JOIN ZONAS_CLIENTES z ON z.ZONA_CLIENTE_ID = c.ZONA_CLIENTE_ID
            LEFT JOIN VENDEDORES v ON v.VENDEDOR_ID = c.VENDEDOR_ID
            {clausulaWhere}
            """;

        try
        {
            await using var conn = new FbConnection(connectionString);

            var total = await conn.ExecuteScalarAsync<int>(
                new CommandDefinition($"SELECT COUNT(*) FROM ({baseSql})", parametros, cancellationToken: cancellationToken));

            var filas = await conn.QueryAsync<ClienteFila>(
                new CommandDefinition(
                    $"{baseSql} ORDER BY c.NOMBRE ASC ROWS @inicio TO @fin",
                    new { inicio = (page - 1) * perPage + 1, fin = page * perPage },
                    cancellationToken: cancellationToken));

            var items = filas.Select(f => new CustomerListItemDto(
                f.ClienteId,
                f.ClienteId.ToString(),
                f.Nombre,
                f.ZonaClienteId,
                f.ZonaNombre,
                f.VendedorId,
                f.VendedorNombre,
                f.VendedorNombre,
                f.Estatus)).ToList();

            return WebCustomerResult.Exito(new WebListResponse<CustomerListItemDto>(
                items,
                WebPageMeta.Create(page, perPage, total),
                new { page, per_page = perPage, search = query.Search, zone_id = query.ZoneId, route_id = query.RouteId, status = query.Status }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar clientes (zone={ZoneId}, route={RouteId})", query.ZoneId, query.RouteId);
            return WebCustomerResult.Error("API_UNAVAILABLE", "No se pudo consultar el catálogo de clientes.");
        }
    }

    private static (int Page, int PerPage) NormalizarPaginacion(int page, int perPage)
    {
        var p = Math.Clamp(page, CustomerListQuery.MinPage, int.MaxValue);
        var pp = Math.Clamp(perPage, CustomerListQuery.MinPerPage, CustomerListQuery.MaxPerPage);
        return (p, pp);
    }

    private static string EscapeLike(string valor)
        => valor.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    private sealed record ClienteFila(
        int ClienteId,
        string Nombre,
        string Estatus,
        int? ZonaClienteId,
        string? ZonaNombre,
        int? VendedorId,
        string? VendedorNombre);
}