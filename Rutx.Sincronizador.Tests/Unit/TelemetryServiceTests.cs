using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;

namespace Rutx.Sincronizador.Tests.Unit;

public class TelemetryServiceTests
{
    private readonly Mock<IWebSqliteStore> _storeMock;
    private readonly TelemetryService _service;

    public TelemetryServiceTests()
    {
        _storeMock = new Mock<IWebSqliteStore>();
        _service = new TelemetryService(_storeMock.Object);
    }

    [Fact]
    public async Task ProcessEvent_InvalidEventType_ThrowsArgumentException()
    {
        var req = new TelemetryEventRequest("evt-1", "invalid_type", null, null, null, null, null, "2026-08-28T12:00:00Z", "dev-1", null);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ProcessEventAsync(req, 1));
    }

    [Fact]
    public async Task ProcessEvent_VentaSinCliente_ThrowsArgumentException()
    {
        var req = new TelemetryEventRequest("evt-1", "venta", null, null, null, null, null, "2026-08-28T12:00:00Z", "dev-1", null);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ProcessEventAsync(req, 1)); // sellerId = 1
    }

    [Fact]
    public async Task ProcessEvent_MetadataExceedsLimit_ThrowsArgumentException()
    {
        var meta = new Dictionary<string, object?>();
        for (int i = 0; i < 21; i++) meta[$"k{i}"] = "v";

        var req = new TelemetryEventRequest("evt-1", "descarga_matutina", null, null, null, null, null, "2026-08-28T12:00:00Z", "dev-1", meta);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.ProcessEventAsync(req, 1));
    }

    [Fact]
    public async Task ProcessEvent_Valid_ReturnsProcessed()
    {
        _storeMock.Setup(s => s.RegisterEventAsync(It.IsAny<SellerEventRow>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(("inserted", new SellerEventRow(5, "evt-1", "descarga_matutina", 100, null, null, null, "hash", null, "time", "status", "", "")));

        var req = new TelemetryEventRequest("evt-1", "descarga_matutina", null, null, 19.4, -99.1, 10.5, "2026-08-28T12:00:00Z", "dev-1", null);
        var resp = await _service.ProcessEventAsync(req, 100);

        Assert.Equal("processed", resp.Status);
        Assert.False(resp.Duplicate);

        _storeMock.Verify(s => s.InsertLocationForEventAsync(5, 100, null, 19.4, -99.1, 10.5, "2026-08-28T12:00:00Z", "descarga_matutina", null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
