namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/reports/sales-comparison (contrato v2 §6.3):
/// dos series comparables (periodo actual vs. mismo periodo del año anterior).
/// </summary>
public class ComparisonResponse
{
    public List<SalesPointDto> Current { get; set; } = new();

    public List<SalesPointDto> Previous { get; set; } = new();

    public string Currency { get; set; } = "MXN";

    public string Status { get; set; } = "unknown";
}
