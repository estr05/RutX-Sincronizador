using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rutx.Sincronizador.Controllers.Web;
using Rutx.Sincronizador.Models.Web.Auth;
using Rutx.Sincronizador.Services.Web;
using System.Security.Claims;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del controlador de autenticación web: envelope v2, códigos
/// funcionales (401/403), propagación de trace_id y contratos me/logout.
/// </summary>
public class WebAuthControllerTests
{
    private static WebAuthController CrearController(IWebAuthService service)
    {
        var controller = new WebAuthController(service, NullLogger<WebAuthController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
            },
        };
        return controller;
    }

    private static WebLoginResponse SesionFalsa() => new(
        "token-de-prueba",
        "2026-08-16T12:00:00Z",
        new WebUserSessionDto(1, "admin.coyatoc", "Admin Coyatoc", false, true, new[] { "administrador" }, new[] { "customers.read" }, Array.Empty<int>()),
        new[] { "administrador" },
        new[] { "customers.read" },
        Array.Empty<int>());

    [Fact]
    public async Task Login_Exitoso_Devuelve200ConEnvelopeYToken()
    {
        var service = new Mock<IWebAuthService>();
        service
            .Setup(s => s.LoginAsync(It.IsAny<WebLoginRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebAuthResult.Exito(SesionFalsa()));

        var controller = CrearController(service.Object);

        var result = await controller.Login(new WebLoginRequest("admin.coyatoc", "Clave!123"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value);
        Assert.Contains("data", envelope.Keys);
        Assert.Contains("trace_id", envelope.Keys);
        var data = Assert.IsAssignableFrom<WebLoginResponse>(envelope["data"]);
        Assert.Equal("web", data.Scope);
        Assert.Equal("token-de-prueba", data.AccessToken);
    }

    [Fact]
    public async Task Login_CredencialesInvalidas_Devuelve401ConCodigoFuncional()
    {
        var service = new Mock<IWebAuthService>();
        service
            .Setup(s => s.LoginAsync(It.IsAny<WebLoginRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebAuthResult.Error("UNAUTHENTICATED", "Usuario o contraseña incorrectos."));

        var controller = CrearController(service.Object);

        var result = await controller.Login(new WebLoginRequest("x", "y"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(401, status.StatusCode);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(status.Value);
        Assert.Equal("UNAUTHENTICATED", envelope["code"]);
        Assert.Contains("trace_id", envelope.Keys);
    }

    [Fact]
    public async Task Login_CuentaDeshabilitada_Devuelve403()
    {
        var service = new Mock<IWebAuthService>();
        service
            .Setup(s => s.LoginAsync(It.IsAny<WebLoginRequest>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(WebAuthResult.Error("ACCOUNT_DISABLED", "Cuenta deshabilitada. Contacta al administrador."));

        var controller = CrearController(service.Object);

        var result = await controller.Login(new WebLoginRequest("inactivo", "x"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.Equal("ACCOUNT_DISABLED", Assert.IsAssignableFrom<Dictionary<string, object?>>(status.Value)["code"]);
    }

    [Fact]
    public void Me_ConPrincipalValido_DevuelveSesion()
    {
        var service = new Mock<IWebAuthService>();
        service
            .Setup(s => s.Me(It.IsAny<ClaimsPrincipal>()))
            .Returns(WebAuthResult.Exito(SesionFalsa()));

        var controller = CrearController(service.Object);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("username", "admin.coyatoc") }, "test"));

        var result = controller.Me();

        var ok = Assert.IsType<OkObjectResult>(result);
        var envelope = Assert.IsAssignableFrom<Dictionary<string, object?>>(ok.Value);
        Assert.Contains("data", envelope.Keys);
    }

    [Fact]
    public async Task Logout_Devuelve204()
    {
        var service = new Mock<IWebAuthService>();
        service
            .Setup(s => s.LogoutAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var controller = CrearController(service.Object);

        var result = await controller.Logout(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        service.Verify(s => s.LogoutAuditAsync(null, null, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}