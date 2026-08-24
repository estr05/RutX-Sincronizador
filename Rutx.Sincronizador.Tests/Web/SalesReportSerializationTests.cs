using System.Text.Json;
using Rutx.Sincronizador.Models.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Contrato JSON de GET /api/v2/web/reports/sales (contrato v2 §6.3 y §9.1):
/// SalesTotalsDto debe exponer los seis importes que consume la pantalla web
/// (sales_amount, cash_amount, credit_amount, total_amount, pieces, currency)
/// y by_route la forma completa por fila, todo en snake_case igual que la
/// política SnakeCaseLower de Program.cs. Si falta un campo, la web renderiza
/// ceros en el footer aunque existan datos.
/// </summary>
public class SalesReportSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void SalesTotalsDto_SerializaLosSeisCamposDelContrato()
    {
        var dto = new SalesTotalsDto
        {
            SalesAmount = 410m,
            CashAmount = 410m,
            CreditAmount = 0m,
            TotalAmount = 410m,
            Pieces = 7,
            Currency = "MXN",
        };

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains("\"sales_amount\":410", json);
        Assert.Contains("\"cash_amount\":410", json);
        Assert.Contains("\"credit_amount\":0", json);
        Assert.Contains("\"total_amount\":410", json);
        Assert.Contains("\"pieces\":7", json);
        Assert.Contains("\"currency\":\"MXN\"", json);
    }

    [Fact]
    public void RouteSalesAggregateDto_SerializaLaFormaCompletaPorFila()
    {
        var dto = new RouteSalesAggregateDto
        {
            RouteName = "Ruta Norte",
            Pieces = 7,
            CashAmount = 410m,
            CreditAmount = 0m,
            TotalAmount = 410m,
        };

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains("\"route_name\":\"Ruta Norte\"", json);
        Assert.Contains("\"pieces\":7", json);
        Assert.Contains("\"cash_amount\":410", json);
        Assert.Contains("\"credit_amount\":0", json);
        Assert.Contains("\"total_amount\":410", json);
    }

    [Fact]
    public void SalesReportResponse_ExponeTotalsByRouteYStatus()
    {
        var dto = new SalesReportResponse
        {
            Totals = new SalesTotalsDto { Pieces = 1 },
            ByRoute = { new RouteSalesAggregateDto { RouteName = "A", Pieces = 1 } },
            Status = "ok",
        };

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains("\"totals\"", json);
        Assert.Contains("\"by_route\"", json);
        Assert.Contains("\"status\":\"ok\"", json);
    }
}
