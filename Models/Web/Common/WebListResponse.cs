namespace Rutx.Sincronizador.Models.Web.Common;

/// <summary>
/// Envelope genérico de listados del contrato v2 (§3.3): data, meta,
/// filters y trace_id. El controlador lo envuelve con WebEnvelope.Success.
/// </summary>
public sealed record WebListResponse<T>(
    IReadOnlyList<T> Data,
    WebPageMeta Meta,
    object? Filters = null);

/// <summary>
/// Metadatos de paginación del contrato v2 (§3.2): page, per_page, total, last_page.
/// </summary>
public sealed record WebPageMeta(
    int Page,
    int PerPage,
    int Total,
    int LastPage)
{
    public static WebPageMeta Create(int page, int perPage, int total) => new(
        page,
        perPage,
        total,
        (int)Math.Ceiling(total / (double)perPage));
}