using Microsoft.AspNetCore.Mvc;

namespace Rutx.Sincronizador.Models.Web.Inventory;

/// <summary>
/// Consulta del piloto GET /api/v2/web/inventory/by-route (contrato v2 §6.4,
/// promovido a piloto Sprint 4 — decisión aprobada en el Bloque 0).
/// route_id ≡ VENDEDOR_ID; el almacén de la ruta se resuelve con la operación
/// real (DOCTOS_PV más reciente) y as_of es el periodo "YYYY-MM" (ISO 8601).
/// </summary>
public sealed record InventoryRouteQuery(
    [FromQuery(Name = "page")] int Page = 1,
    [FromQuery(Name = "per_page")] int PerPage = 25,
    [FromQuery(Name = "zone_id")] int? ZoneId = null,
    [FromQuery(Name = "route_id")] int? RouteId = null,
    [FromQuery(Name = "as_of")] string? AsOf = null)
{
    public const int MinPage = 1;
    public const int MinPerPage = 1;
    public const int MaxPerPage = 100;
}

/// <summary>
/// Fila del piloto de inventario por ruta (solo lectura).
/// disponible  = Σ(ENTRADAS_UNIDADES − SALIDAS_UNIDADES) histórico del ledger
///               SALDOS_IN para el almacén de la ruta (fórmula aprobada §12.2).
/// vendido     = SALIDAS_UNIDADES del periodo as_of (informativo, no se resta).
/// diferencia  = disponible − vendido (informativo; NO se usa para existencias).
/// </summary>
public sealed record InventoryRouteRowDto(
    int ProductId,
    string ProductCode,
    string ProductName,
    string Unit,
    decimal Available,
    decimal Sold,
    decimal Difference);