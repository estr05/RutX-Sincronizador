using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rutx.Sincronizador.Controllers.Web;
using Rutx.Sincronizador.Models.Web.Common;
using Rutx.Sincronizador.Models.Web.Customers;
using Rutx.Sincronizador.Models.Web.Inventory;
using Rutx.Sincronizador.Models.Web.Notifications;
using Rutx.Sincronizador.Services.Web;
using System.Security.Claims;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del catálogo de clientes: envelope v2 con meta/filters y el
/// mapeo de códigos funcionales (FORBIDDEN_ZONE → 403, VALIDATION → 422).
/// </summary>
public class CustomersControllerTests
{
    private static CustomersController CrearController(ICustomerWebService service)
    {
        var controller = new CustomersController(service, NullLogger<CustomersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    private static WebCustomerResult ListaFalsa() => WebCustomerResult.Exito(
        new WebListResponse<CustomerListItemDto>(
            new[]
            {
                new CustomerListItemDto(99005, "99005", "ABARROTERA DEL ESTE", 3792, "SUR", 695, "695", "Vendedor 695", "A"),
            },
            WebPageMeta.Create(1, 25, 1),
            new { page = 1, per_page = 25, zone_id = (int?)null, route_id = (int?)null, status = (string?)null, search = (string?)null }));

    [Fact]
    public async Task GetCustomers_DevuelveEnvelopeConDataMetaYFilters()
    {
        var service = new Mock<ICustomerWebService>();
        service
            .Setup(s => s.ListAsync(It.IsAny<CustomerListQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ListaFalsa());

        var controller = CrearController(service.Object);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("zone_ids", "3792") }, "test"));

        var result = await controller.GetCustomers(new CustomerListQuery(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value);
        Assert.Contains("data", envelope.Keys);
        Assert.Contains("meta", envelope.Keys);
        Assert.Contains("filters", envelope.Keys);
        Assert.Contains("trace_id", envelope.Keys);
        var data = Assert.IsAssignableFrom<IReadOnlyList<CustomerListItemDto>>(envelope["data"]);
        Assert.Single(data);
        Assert.Equal("99005", data[0].Code);
    }

    [Fact]
    public async Task GetCustomers_ZonaProhibida_Devuelve403ConCodigoFuncional()
    {
        var service = new Mock<ICustomerWebService>();
        service
            .Setup(s => s.ListAsync(It.IsAny<CustomerListQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebCustomerResult.Error("FORBIDDEN_ZONE", "La zona solicitada no está dentro de las zonas autorizadas de tu usuario."));

        var controller = CrearController(service.Object);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("zone_ids", "3793") }, "test"));

        var result = await controller.GetCustomers(new CustomerListQuery { ZoneId = 3792 }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.Equal("FORBIDDEN_ZONE", Assert.IsAssignableFrom<Dictionary<string, object?>>(status.Value)["code"]);
    }
}

/// <summary>
/// Pruebas del piloto de inventario por ruta: validación de as_of,
/// FORBIDDEN_ZONE y NOT_FOUND (ruta sin almacén de operación).
/// </summary>
public class InventoryControllerTests
{
    private static InventoryController CrearController(IInventoryWebService service)
    {
        var controller = new InventoryController(service, NullLogger<InventoryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        return controller;
    }

    [Fact]
    public async Task GetByRoute_DevuelveEnvelopeConData()
    {
        var service = new Mock<IInventoryWebService>();
        service
            .Setup(s => s.ListByRouteAsync(It.IsAny<InventoryRouteQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebInventoryResult.Exito(
                new WebListResponse<InventoryRouteRowDto>(
                    new[] { new InventoryRouteRowDto(312, "312", "CHOCOLATE ABARROTERO", "PZA", 4m, 16m, -12m) },
                    WebPageMeta.Create(1, 25, 1),
                    new { page = 1, per_page = 25, route_id = 695, almacen = 9711 })));

        var controller = CrearController(service.Object);

        var result = await controller.GetByRoute(new InventoryRouteQuery { RouteId = 695 }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("data", Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value).Keys);
        service.Verify(s => s.ListByRouteAsync(It.IsAny<InventoryRouteQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetByRoute_ErrorDeServicio_Devuelve403OCodigoFuncional()
    {
        var service = new Mock<IInventoryWebService>();
        service
            .Setup(s => s.ListByRouteAsync(It.IsAny<InventoryRouteQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebInventoryResult.Error("FORBIDDEN_ZONE", "La zona solicitada no está dentro de las zonas autorizadas de tu usuario."));

        var controller = CrearController(service.Object);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("zone_ids", "3793") }, "test"));

        var result = await controller.GetByRoute(new InventoryRouteQuery { RouteId = 695, ZoneId = 3792 }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }
}

/// <summary>
/// Pruebas de la bandeja y emisión de notificaciones: 201 con Idempotency-Key,
/// 409 por clave repetida, 422 sin clave y 403 por zona fuera del alcance.
/// </summary>
public class NotificationsControllerTests
{
    private static NotificationsController CrearController(INotificationWebService service, string? idempotencyKey = null)
    {
        var http = new DefaultHttpContext();
        if (idempotencyKey != null)
            http.Request.Headers["Idempotency-Key"] = idempotencyKey;

        var controller = new NotificationsController(service, NullLogger<NotificationsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return controller;
    }

    [Fact]
    public async Task GetNotifications_DevuelveEnvelopeConData()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.ListAsync(It.IsAny<NotificationListQuery>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebNotificationResult.Exito(
                new WebListResponse<NotificationListItemDto>(
                    new[] { new NotificationListItemDto(1, "seller", 695, "Reunión", "Viernes 9:00", "normal", "active", "admin.coyatoc", "2026-08-16T10:00:00Z") },
                    WebPageMeta.Create(1, 25, 1),
                    new { page = 1, per_page = 25 })));

        var controller = CrearController(service.Object);

        var result = await controller.GetNotifications(new NotificationListQuery(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("meta", Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value).Keys);
    }

    [Fact]
    public async Task GetCount_DevuelveElConteoActivo()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.CountActiveAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var controller = CrearController(service.Object);

        var result = await controller.GetCount(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value);
        var data = Assert.IsAssignableFrom<NotificationCountDto>(envelope["data"]);
        Assert.Equal(3, data.ActiveCount);
    }

    [Fact]
    public async Task Create_ConIdempotencyKey_Devuelve201()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<NotificationCreateRequest>(), "clave-única", It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebNotificationResult.Exito(new { created_count = 2, notification_ids = new[] { 1L, 2L } }));

        var controller = CrearController(service.Object, idempotencyKey: "clave-única");

        var result = await controller.Create(new NotificationCreateRequest("seller", new[] { 695, 9647 }, "Reunión", "Viernes 9:00"), CancellationToken.None);

        var created = Assert.IsType<ObjectResult>(result);
        Assert.Equal(201, created.StatusCode);
        service.Verify(s => s.CreateAsync(It.IsAny<NotificationCreateRequest>(), "clave-única", It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_SinIdempotencyKey_LaClaveSePasaComoNull()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<NotificationCreateRequest>(), null, It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebNotificationResult.Error("VALIDATION_ERROR", "El encabezado Idempotency-Key es obligatorio para emitir notificaciones."));

        var controller = CrearController(service.Object);

        var result = await controller.Create(new NotificationCreateRequest("seller", new[] { 695 }, "T", ""), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(422, status.StatusCode);
        Assert.Equal("VALIDATION_ERROR", Assert.IsAssignableFrom<Dictionary<string, object?>>(status.Value)["code"]);
    }

    [Fact]
    public async Task Create_ClaveRepetida_Devuelve409()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<NotificationCreateRequest>(), "repetida", It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebNotificationResult.Error("IDEMPOTENCY_CONFLICT", "Esta Idempotency-Key ya fue utilizada para una emisión previa."));

        var controller = CrearController(service.Object, idempotencyKey: "repetida");

        var result = await controller.Create(new NotificationCreateRequest("seller", new[] { 695 }, "T", ""), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(409, status.StatusCode);
    }

    [Fact]
    public async Task Create_ZonaFueraDeAlcance_Devuelve403()
    {
        var service = new Mock<INotificationWebService>();
        service
            .Setup(s => s.CreateAsync(It.IsAny<NotificationCreateRequest>(), "clave-zona", It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<int>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebNotificationResult.Error("FORBIDDEN_ZONE", "Solo puedes emitir avisos a zonas dentro de las zonas autorizadas de tu usuario."));

        var controller = CrearController(service.Object, idempotencyKey: "clave-zona");

        var result = await controller.Create(new NotificationCreateRequest("zone", new[] { 3795 }, "T", ""), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
    }
}