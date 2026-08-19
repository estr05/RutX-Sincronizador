using Microsoft.AspNetCore.Mvc;

namespace Rutx.Sincronizador.Models.Web.Customers;

/// <summary>
/// Consulta de GET /api/v2/web/customers (contrato v2 §3.2).
/// route_id ≡ VENDEDOR_ID (decisión Bloque 0 §12.1). Límites de paginación
/// aplicados por el servicio; la autorización de zona vive en el servicio.
/// </summary>
public sealed record CustomerListQuery(
    [FromQuery(Name = "page")] int Page = 1,
    [FromQuery(Name = "per_page")] int PerPage = 25,
    [FromQuery(Name = "search")] string? Search = null,
    [FromQuery(Name = "zone_id")] int? ZoneId = null,
    [FromQuery(Name = "route_id")] int? RouteId = null,
    [FromQuery(Name = "status")] string? Status = null)
{
    public const int MinPage = 1;
    public const int MinPerPage = 1;
    public const int MaxPerPage = 100;
}

/// <summary>
/// Fila del listado de clientes. Campos de contacto/ubicación NO se exponen
/// hasta existir customer.contact.read y customer.location.read (§5.1).
/// code es CLIENTE_ID: la tabla CLIENTES de Microsip no tiene columna CODIGO
/// (verificado en Firebird) — se documenta y no se inventa un código.
/// </summary>
public sealed record CustomerListItemDto(
    int CustomerId,
    string Code,
    string Name,
    int? ZoneId,
    string? ZoneName,
    int? RouteId,
    string? RouteName,
    string? SellerName,
    string Status);