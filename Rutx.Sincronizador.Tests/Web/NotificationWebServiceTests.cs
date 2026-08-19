using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models.Web.Notifications;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del servicio de notificaciones: idempotencia estricta
/// (Idempotency-Key obligatoria y única), validación de comando y
/// alcance por zonas autorizadas en el servidor.
/// </summary>
public class NotificationWebServiceTests
{
    private static NotificationWebService CrearServicio(IWebSqliteStore store)
        => new(store, NullLogger<NotificationWebService>.Instance);

    private static readonly NotificationCreateRequest RequestValido =
        new("seller", new[] { 695 }, "Reunión", "Viernes 9:00", "normal");

    [Fact]
    public async Task Create_SinIdempotencyKey_DevuelveVALIDATION()
    {
        var store = new Mock<IWebSqliteStore>();
        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido, null, 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.False(resultado.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", resultado.Code);
        store.Verify(s => s.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_ClaveYaUsada_DevuelveCONFLICTSinDuplicar()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindNotificationByIdempotencyAsync("clave-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebNotificationRow(1, "seller", 695, "Reunión", "Viernes 9:00", "normal", "active", 1, "admin.coyatoc", "clave-1", "trace", "2026-08-16"));

        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido, "clave-1", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.False(resultado.IsSuccess);
        Assert.Equal("IDEMPOTENCY_CONFLICT", resultado.Code);
        store.Verify(s => s.CreateNotificationsBatchAsync(It.IsAny<IReadOnlyList<NotificationBatchItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_TargetTypeInvalido_DevuelveVALIDATION()
    {
        var store = new Mock<IWebSqliteStore>();
        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetType = "grupo" }, "clave-ok", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Create_SinDestinatarios_DevuelveVALIDATION()
    {
        var store = new Mock<IWebSqliteStore>();
        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetIds = Array.Empty<int>() }, "clave-ok", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Create_MasDe50Destinatarios_DevuelveVALIDATION()
    {
        var store = new Mock<IWebSqliteStore>();
        var servicio = CrearServicio(store.Object);
        var ids = Enumerable.Range(1, 51).ToArray();

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetIds = ids }, "clave-ok", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Create_ZonaFueraDelAlcanceDelUsuario_DevuelveFORBIDDEN()
    {
        var store = new Mock<IWebSqliteStore>();
        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetType = "zone", TargetIds = new[] { 3795 } },
            "clave-ok", 1, "admin.coyatoc", new[] { 3792, 3793 }, "trace", "ip");

        Assert.False(resultado.IsSuccess);
        Assert.Equal("FORBIDDEN_ZONE", resultado.Code);
        store.Verify(s => s.CreateNotificationsBatchAsync(It.IsAny<IReadOnlyList<NotificationBatchItem>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_UsuarioSinRestriccionDeZonas_PuedeEmitirACualquierZona()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindNotificationByIdempotencyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WebNotificationRow?)null);
        store
            .Setup(s => s.CreateNotificationsBatchAsync(It.IsAny<IReadOnlyList<NotificationBatchItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NotificationBatchItem> items, CancellationToken _) =>
                items.Select((it, i) => new WebNotificationRow(i + 1, it.TargetType, it.TargetId, it.Title, it.Body, it.Priority, "active", it.SenderUserId, it.SenderUsername, it.IdempotencyKey, it.TraceId, "2026-08-18")).ToList());

        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetType = "zone", TargetIds = new[] { 3795 } },
            "clave-ok", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.True(resultado.IsSuccess);
        var creado = Assert.IsType<NotificationCreateResult>(resultado.Response);
        Assert.Equal(1, creado.CreatedCount);
    }

    [Fact]
    public async Task Create_Valido_CreaUnaFilaPorDestinatario()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindNotificationByIdempotencyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WebNotificationRow?)null);
        store
            .Setup(s => s.CreateNotificationsBatchAsync(It.IsAny<IReadOnlyList<NotificationBatchItem>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<NotificationBatchItem> items, CancellationToken _) =>
                items.Select((it, i) => new WebNotificationRow(i + 1, it.TargetType, it.TargetId, it.Title, it.Body, it.Priority, "active", it.SenderUserId, it.SenderUsername, it.IdempotencyKey, it.TraceId, "2026-08-18")).ToList());

        var servicio = CrearServicio(store.Object);
        var ids = new[] { 695, 9647, 695 };

        var resultado = await servicio.CreateAsync(
            RequestValido with { TargetIds = ids }, "clave-ok", 1, "admin.coyatoc", Array.Empty<int>(), "trace", "ip");

        Assert.True(resultado.IsSuccess);
        // ids.Distinct() → 2 filas.
        var creado = Assert.IsType<NotificationCreateResult>(resultado.Response);
        Assert.Equal(2, creado.CreatedCount);
        store.Verify(s => s.CreateNotificationsBatchAsync(
            It.Is<IReadOnlyList<NotificationBatchItem>>(items =>
                items.Count == 2 &&
                items[0].TargetType == "seller" && items[0].TargetId == 695 && items[0].IdempotencyKey == "clave-ok" &&
                items[1].TargetType == "seller" && items[1].TargetId == 9647 && items[1].IdempotencyKey == ""),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_RespetaElAlcancePorZonasDelUsuario()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.ListNotificationsAsync(null, null, null, new[] { 3792 }, 1, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync((new List<WebNotificationRow>(), 0));

        var servicio = CrearServicio(store.Object);

        var resultado = await servicio.ListAsync(new NotificationListQuery(), new[] { 3792 }, CancellationToken.None);

        Assert.True(resultado.IsSuccess);
        store.Verify(s => s.ListNotificationsAsync(null, null, null, new[] { 3792 }, 1, 25, It.IsAny<CancellationToken>()), Times.Once);
    }
}