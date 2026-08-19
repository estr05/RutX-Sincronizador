namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/dashboard/sales-series (contrato v2 §6.1):
/// serie de ventas por periodo para <x-chart>.
/// </summary>
public class SalesSeriesResponse
{
    public List<SalesPointDto> Series { get; set; } = new();

    public string Currency { get; set; } = "MXN";

    public string Status { get; set; } = "unknown";
}

/// <summary>
/// Punto de la serie: periodo (Y-m-d) y monto del periodo.
/// </summary>
public class SalesPointDto
{
    public string Period { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
