using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rutx.Sincronizador.Controllers.Web;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Verifica el flujo obligatorio Controller Web → Interface Web → Service Web:
/// el controller delega en la interfaz y devuelve el envelope v2 con data y
/// trace_id (sin exponer excepciones internas).
/// </summary>
public class DashboardControllerTests
{
    [Fact]
    public async Task GetDashboard_DelegaEnInterfaceYDevuelveEnvelopeConData()
    {
        var service = new Mock<IDashboardWebService>();
        service
            .Setup(s => s.ObtenerResumenAsync(
                It.IsAny<ReportFilterQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardSummaryResponse
            {
                Kpi = { new DashboardKpiDto { Label = "Venta total", Value = 1500m, Status = "ok" } },
                Meta = new DashboardMetaDto { Currency = "MXN" },
            });

        var controller = new DashboardController(service.Object, NullLogger<DashboardController>.Instance);

        var result = await controller.GetDashboard(new ReportFilterQuery(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value);
        Assert.Contains("data", envelope.Keys);
        Assert.Contains("trace_id", envelope.Keys);
        Assert.NotNull(envelope["trace_id"]);
        service.Verify(
            s => s.ObtenerResumenAsync(It.IsAny<ReportFilterQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetDashboard_CuandoElServicioFalla_Devuelve500SinTrazas()
    {
        var service = new Mock<IDashboardWebService>();
        service
            .Setup(s => s.ObtenerResumenAsync(
                It.IsAny<ReportFilterQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("detalle interno de BD"));

        var controller = new DashboardController(service.Object, NullLogger<DashboardController>.Instance);

        var result = await controller.GetDashboard(new ReportFilterQuery(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, status.StatusCode);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(status.Value);
        Assert.Contains("code", envelope.Keys);
        Assert.Contains("message", envelope.Keys);
        Assert.Contains("trace_id", envelope.Keys);
        Assert.DoesNotContain("detalle interno", envelope.Values.Select(v => v?.ToString()));
    }

    [Fact]
    public async Task GetDashboard_PasaLasZonasDelUsuarioAlServicio()
    {
        IReadOnlyList<int>? zonasRecibidas = null;
        var service = new Mock<IDashboardWebService>();
        service
            .Setup(s => s.ObtenerResumenAsync(
                It.IsAny<ReportFilterQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .Callback<ReportFilterQuery, IReadOnlyList<int>, CancellationToken>((_, zonas, _) => zonasRecibidas = zonas)
            .ReturnsAsync(new DashboardSummaryResponse());

        var controller = new DashboardController(service.Object, NullLogger<DashboardController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim("zone_ids", "3792"),
                        new Claim("zone_ids", "3793"),
                    }, "test")),
                },
            },
        };

        await controller.GetDashboard(new ReportFilterQuery(), CancellationToken.None);

        Assert.Equal(new[] { 3792, 3793 }, zonasRecibidas);
    }
}
