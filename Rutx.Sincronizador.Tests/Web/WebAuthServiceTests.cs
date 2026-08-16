using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Rutx.Sincronizador.Data.Web;
using Rutx.Sincronizador.Models.Web.Auth;
using Rutx.Sincronizador.Security;
using Rutx.Sincronizador.Security.Web;
using Rutx.Sincronizador.Services.Web;
using System.Security.Claims;
using System.Security.Cryptography;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas del vertical de autenticación del portal (contrato v2 §5):
/// login contra web_users (nunca stubs), resolución rol → permisos
/// (WebRoleCatalog), zonas autorizadas, rehash por rotación y la
/// exigencia scope=web del handler de permisos.
/// </summary>
public class WebAuthServiceTests
{
    private static IConfiguration ConfigurarJwt() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "ClaveDePruebaCon32CaracteresMinimos123456",
            ["Jwt:Issuer"] = "RutxSincronizador",
            ["Jwt:Audience"] = "RutxWeb",
        })
        .Build();

    [Fact]
    public async Task Login_CredencialesValidas_EmiteTokenConPermisosYEscopeWeb()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindUserByUsernameAsync("admin.coyatoc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebUserRow(
                1, "admin.coyatoc", "Admin Coyatoc", WebPasswordHasher.Hash("Clave!123"),
                """["administrador"]""", "[3792, 3793]", true, null, true, "2026-01-01", "2026-01-01"));
        store
            .Setup(s => s.LogAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebAuditEntry(1, null, null, "auth.login", null, null, null, "2026-01-01"));

        var service = new WebAuthService(store.Object, ConfigurarJwt(), NullLogger<WebAuthService>.Instance);

        var resultado = await service.LoginAsync(new WebLoginRequest("admin.coyatoc", "Clave!123"), "127.0.0.1", "trace1");

        Assert.True(resultado.IsSuccess);
        Assert.NotNull(resultado.Response);
        Assert.Equal("web", resultado.Response!.Scope);
        Assert.NotEmpty(resultado.Response.AccessToken);
        Assert.Contains("customers.read", resultado.Response.Permissions);
        Assert.Contains("inventory.read", resultado.Response.Permissions);
        Assert.Contains("notifications.send", resultado.Response.Permissions);
        Assert.Equal(new[] { 3792, 3793 }, resultado.Response.ZoneIds);
        Assert.Equal("admin.coyatoc", resultado.Response.User.Username);
        Assert.True(resultado.Response.User.MustChangePassword);
        store.Verify(s => s.LogAuditAsync(1, "admin.coyatoc", "auth.login", "exitoso", "127.0.0.1", "trace1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Login_ContrasenaIncorrecta_NoRevelaExistenciaDelUsuario()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindUserByUsernameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WebUserRow?)null);
        store
            .Setup(s => s.LogAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebAuditEntry(1, null, null, "auth.login", null, null, null, "2026-01-01"));

        var service = new WebAuthService(store.Object, ConfigurarJwt(), NullLogger<WebAuthService>.Instance);

        var resultado = await service.LoginAsync(new WebLoginRequest("inexistente", "x"), null, null);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("UNAUTHENTICATED", resultado.Code);
        Assert.Equal("Usuario o contraseña incorrectos.", resultado.Message);
    }

    [Fact]
    public async Task Login_CuentaDeshabilitada_DevuelveCuentaDeshabilitada()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindUserByUsernameAsync("inactivo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebUserRow(
                2, "inactivo", "Inactivo", WebPasswordHasher.Hash("Clave!123"),
                """["lector"]""", "[]", false, null, false, "2026-01-01", "2026-01-01"));
        store
            .Setup(s => s.LogAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebAuditEntry(1, null, null, "auth.login", null, null, null, "2026-01-01"));

        var service = new WebAuthService(store.Object, ConfigurarJwt(), NullLogger<WebAuthService>.Instance);

        var resultado = await service.LoginAsync(new WebLoginRequest("inactivo", "Clave!123"), null, null);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("ACCOUNT_DISABLED", resultado.Code);
    }

    [Fact]
    public async Task Login_HashAntiguo_RotaElHashSinTocarLaCredencial()
    {
        // Hash válido del formato legacy (iteraciones por debajo del estándar):
        // se genera con 100.000 iteraciones como hacían las versiones viejas.
        var sal = RandomNumberGenerator.GetBytes(16);
        var derivado = Rfc2898DeriveBytes.Pbkdf2("Clave!123", sal, 100_000, HashAlgorithmName.SHA256, 32);
        var hashAntiguo = $"pbkdf2-sha256$100000${Convert.ToBase64String(sal)}${Convert.ToBase64String(derivado)}";
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.FindUserByUsernameAsync("rotar", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebUserRow(
                3, "rotar", "Rotar", hashAntiguo,
                """["supervisor"]""", "[]", false, null, true, "2026-01-01", "2026-01-01"));
        store
            .Setup(s => s.LogAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebAuditEntry(1, null, null, "auth.login", null, null, null, "2026-01-01"));

        var service = new WebAuthService(store.Object, ConfigurarJwt(), NullLogger<WebAuthService>.Instance);

        var resultado = await service.LoginAsync(new WebLoginRequest("rotar", "Clave!123"), null, null);

        Assert.True(resultado.IsSuccess, $"{resultado.Code}: {resultado.Message}");
        store.Verify(s => s.UpdateUserPasswordAsync(3, It.Is<string>(h => WebPasswordHasher.Verify("Clave!123", h)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Me_ReconstruyeSesionDesdeClaims()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("user_id", "7"),
            new Claim("username", "supervisor.centro"),
            new Claim("display_name", "María Hernández"),
            new Claim("roles", "supervisor"),
            new Claim("permissions", "customers.read"),
            new Claim("permissions", "notifications.read"),
            new Claim("zone_ids", "3793"),
        }, "test"));

        var service = new WebAuthService(
            new Mock<IWebSqliteStore>().Object,
            ConfigurarJwt(),
            NullLogger<WebAuthService>.Instance);

        var resultado = service.Me(principal);

        Assert.True(resultado.IsSuccess);
        Assert.Equal("María Hernández", resultado.Response!.User.DisplayName);
        Assert.Equal(new[] { "customers.read", "notifications.read" }, resultado.Response.Permissions);
        Assert.Equal(new[] { 3793 }, resultado.Response.ZoneIds);
    }

    [Fact]
    public void Me_TokenSinIdentidad_DevuelveUNAUTHENTICATED()
    {
        var service = new WebAuthService(
            new Mock<IWebSqliteStore>().Object,
            ConfigurarJwt(),
            NullLogger<WebAuthService>.Instance);

        var resultado = service.Me(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(resultado.IsSuccess);
        Assert.Equal("UNAUTHENTICATED", resultado.Code);
    }

    [Fact]
    public async Task LogoutAudit_RegistraElCierre()
    {
        var store = new Mock<IWebSqliteStore>();
        store
            .Setup(s => s.LogAuditAsync(It.IsAny<long?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WebAuditEntry(1, null, null, "auth.logout", null, null, null, "2026-01-01"));

        var service = new WebAuthService(store.Object, ConfigurarJwt(), NullLogger<WebAuthService>.Instance);

        await service.LogoutAuditAsync(1, "admin.coyatoc", "127.0.0.1", "trace2");

        store.Verify(s => s.LogAuditAsync(1, "admin.coyatoc", "auth.logout", It.IsAny<string?>(), "127.0.0.1", "trace2", It.IsAny<CancellationToken>()), Times.Once);
    }
}

/// <summary>
/// Verifica que la autorización por permiso exige scope=web y el permiso en
/// los claims (el rol por nombre jamás autoriza por sí solo).
/// </summary>
public class WebPermissionHandlerTests
{
    [Fact]
    public async Task PermisoEnClaimsConScopeWeb_Concede()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("scope", "web"),
            new Claim("permissions", "customers.read"),
        }, "test"));

        var handler = new WebPermissionHandler();
        var context = new AuthorizationHandlerContext(
            new[] { new WebPermissionRequirement("customers.read") },
            principal,
            resource: null);

        await handler.HandleAsync(context);

        Assert.True(context.HasSucceeded);
    }

    [Fact]
    public async Task TokenMovilSinScopeWeb_JamasConcedePermisoWeb()
    {
        // Token móvil: rol por nombre pero sin scope=web ni permisos.
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Role, "administrador"),
            new Claim("rol", "vendedor"),
        }, "test"));

        var handler = new WebPermissionHandler();
        var context = new AuthorizationHandlerContext(
            new[] { new WebPermissionRequirement("customers.read") },
            principal,
            resource: null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }

    [Fact]
    public async Task PermisoAusenteEnClaims_NoConcede()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("scope", "web"),
            new Claim("permissions", "reports.read"),
        }, "test"));

        var handler = new WebPermissionHandler();
        var context = new AuthorizationHandlerContext(
            new[] { new WebPermissionRequirement("customers.read") },
            principal,
            resource: null);

        await handler.HandleAsync(context);

        Assert.False(context.HasSucceeded);
    }
}