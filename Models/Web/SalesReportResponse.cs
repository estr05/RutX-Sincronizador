namespace Rutx.Sincronizador.Models.Web;

/// <summary>
/// Respuesta de GET /api/v2/web/reports/sales (contrato v2 §6.3):
/// agregados de ventas, piezas y montos por ruta.
/// </summary>
public class SalesReportResponse
{
    public SalesTotalsDto Totals { get; set; } = new();

    public List<RouteSalesAggregateDto> ByRoute { get; set; } = new();

    public string Status { get; set; } = "unknown";
}

/// <summary>
/// Totales del período: ventas, piezas y moneda.
/// </summary>
public class SalesTotalsDto
{
    public decimal SalesAmount { get; set; }
    public int Pieces { get; set; }
    public string Currency { get; set; } = "MXN";
}

/// <summary>
/// Agregados por ruta: piezas, contado, crédito y total.
/// </summary>
public class RouteSalesAggregateDto
{
    public string RouteName { get; set; } = string.Empty;
    public int Pieces { get; set; }
    public decimal CashAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal TotalAmount { get; set; }
}
