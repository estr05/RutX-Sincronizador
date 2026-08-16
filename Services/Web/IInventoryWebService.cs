using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Inventory;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Piloto de inventario por ruta (contrato v2 §6.4, aprobado en el Bloque 0).
/// Solo lectura. route_id ≡ VENDEDOR_ID (decisión §12.1).
/// </summary>
public interface IInventoryWebService
{
    /// <summary>
    /// Listado paginado del almacén de la ruta: almacén = ALMACEN_ID más
    /// reciente en DOCTOS_PV para el vendedor (operación real). Sin evidencia
    /// de operación → ErrorNotAvailable (NOT_FOUND) y 404 en el controlador.
    /// </summary>
    Task<WebInventoryResult> ListByRouteAsync(
        InventoryRouteQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default);
}

public sealed record WebInventoryResult(
    bool IsSuccess,
    string? Code,
    string? Message,
    WebListResponse<InventoryRouteRowDto>? Response = null)
{
    public static WebInventoryResult Exito(WebListResponse<InventoryRouteRowDto> response) => new(true, null, null, response);

    public static WebInventoryResult Error(string code, string message) => new(false, code, message, null);
}