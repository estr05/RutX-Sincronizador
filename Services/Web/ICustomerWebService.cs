using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Customers;

namespace Rutx.Sincronizador.Services.Web;

/// <summary>
/// Catálogo de clientes del portal (contrato v2 §6.2, piloto). Consultas
/// parametrizadas sobre Firebird/Microsip; el código del cliente es
/// CLIENTE_ID (Microsip no tiene columna CODIGO — verificado en Firebird).
/// </summary>
public interface ICustomerWebService
{
    /// <summary>
    /// Listado paginado. zone_id solicitado debe estar dentro de las zonas
    /// autorizadas del usuario (claims zone_ids; vacío = sin restricción);
    /// si no, devuelve ErrorForbidden con FORBIDDEN_ZONE.
    /// </summary>
    Task<WebCustomerResult> ListAsync(
        CustomerListQuery query,
        IReadOnlyList<int> userZoneIds,
        CancellationToken cancellationToken = default);
}

public sealed record WebCustomerResult(
    bool IsSuccess,
    string? Code,
    string? Message,
    WebListResponse<CustomerListItemDto>? Response = null)
{
    public static WebCustomerResult Exito(WebListResponse<CustomerListItemDto> response) => new(true, null, null, response);

    public static WebCustomerResult Error(string code, string message) => new(false, code, message, null);
}