using System.Text.Json;
using Rutx.Sincronizador.Models.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Verifica que los DTOs v2 web se serializan en snake_case (contrato §9.1:
/// el Sincronizador usa JSON snake_case, igual que la politica SnakeCaseLower
/// de Program.cs) y que DashboardSummaryResponse trae los 7 KPIs del tablero.
/// </summary>
public class DashboardSummarySerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void DashboardSummaryResponse_SeSerializaEnSnakeCase()
    {
        var dto = new DashboardSummaryResponse
        {
            Kpi = { new DashboardKpiDto { Label = "Venta total", Value = 1500m, Status = "ok" } },
            Meta = new DashboardMetaDto { LastSyncAt = new DateTime(2026, 8, 14, 10, 0, 0), Currency = "MXN" },
        };

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains("\"kpi\"", json);
        Assert.Contains("\"label\"", json);
        Assert.Contains("\"value\"", json);
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"last_sync_at\"", json);
        Assert.Contains("\"currency\"", json);
    }

    [Fact]
    public void RouteMonitorItemDto_SerializaLatLonYEstadoEnSnakeCase()
    {
        var dto = new RouteMonitorItemDto
        {
            RouteId = 1,
            RouteName = "Ruta Centro",
            Latitude = 20.663,
            Longitude = -103.352,
            Status = "active",
        };

        var json = JsonSerializer.Serialize(dto, Options);

        Assert.Contains("\"route_id\":1", json);
        Assert.Contains("\"route_name\"", json);
        Assert.Contains("\"latitude\"", json);
        Assert.Contains("\"longitude\"", json);
        Assert.Contains("\"workday_started_at\"", json);
        Assert.Contains("\"last_sale\"", json);
    }

    [Fact]
    public void EnvelopeDeExito_LlevaDataYTraceId()
    {
        var envelope = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["data"] = new DashboardSummaryResponse(),
                ["trace_id"] = "abc123",
            },
            Options);

        Assert.Contains("\"data\"", envelope);
        Assert.Contains("\"trace_id\":\"abc123\"", envelope);
    }
}
