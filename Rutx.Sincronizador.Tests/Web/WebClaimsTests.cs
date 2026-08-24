using System.Security.Claims;
using Rutx.Sincronizador.Controllers.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Contrato de lectura de claims del portal: un principal nulo o un claim
/// roto jamás concede alcance (regresión del NRE detectado en QA del dashboard).
/// Matriz por rol (contrato §8.1): SOLO administrador tiene alcance global sin
/// zonas; supervisor y lector sin zonas quedan denegados por completo.
/// </summary>
public class WebClaimsTests
{
    private static ClaimsPrincipal PrincipalConZonas(params string[] valores)
    {
        var claims = valores.Select(v => new Claim("zone_ids", v));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal PrincipalConRolYZonas(string rol, params string[] zonas)
    {
        var claims = new List<Claim> { new("roles", rol) };
        claims.AddRange(zonas.Select(z => new Claim("zone_ids", z)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Administrador_SinZonas_TieneAlcanceGlobal()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Administrador));

        Assert.Empty(zonas);
    }

    [Fact]
    public void Supervisor_SinZonas_EstaDenegadoPorCompleto()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Supervisor));

        Assert.Equal(new[] { -1 }, zonas);
    }

    [Fact]
    public void Lector_SinZonas_EstaDenegadoPorCompleto()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Lector));

        Assert.Equal(new[] { -1 }, zonas);
    }

    [Fact]
    public void Administrador_ConZonas_QuedaRestringidoASusZonas()
    {
        // El alcance global solo aplica SIN zonas; con zonas asignadas el admin también se acota.
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Administrador, "3792", "3793"));

        Assert.Equal(new[] { 3792, 3793 }, zonas);
    }

    [Fact]
    public void Supervisor_ConZonas_ResuelveExactamenteSusZonas()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Supervisor, "3792"));

        Assert.Equal(new[] { 3792 }, zonas);
    }

    [Fact]
    public void Lector_ConZonas_ResuelveExactamenteSusZonas()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas(WebRoleCatalog.Lector, "3792", "3793"));

        Assert.Equal(new[] { 3792, 3793 }, zonas);
    }

    [Fact]
    public void RolDesconocido_SinZonas_EstaDenegadoPorCompleto()
    {
        var zonas = WebClaims.Zonas(PrincipalConRolYZonas("contador"));

        Assert.Equal(new[] { -1 }, zonas);
    }

    [Fact]
    public void Zonas_ConPrincipalNulo_DevuelveDenegacion()
    {
        var zonas = WebClaims.Zonas(null);

        Assert.NotNull(zonas);
        Assert.Equal(new[] { -1 }, zonas);
    }

    [Fact]
    public void Zonas_SinIdentidad_DevuelveDenegacion()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // no autenticado, sin claims

        Assert.Equal(new[] { -1 }, WebClaims.Zonas(principal));
    }

    [Fact]
    public void Zonas_ConValoresValidos_LosResuelve()
    {
        var zonas = WebClaims.Zonas(PrincipalConZonas("3792", "3793"));

        Assert.Equal(new[] { 3792, 3793 }, zonas);
    }

    [Fact]
    public void Zonas_DescartaValoresNoNumericos()
    {
        var zonas = WebClaims.Zonas(PrincipalConZonas("3792", "abc", ""));

        Assert.Equal(new[] { 3792 }, zonas);
    }

    [Fact]
    public void Zonas_DescartaValoresNegativos()
    {
        // -1 es el marcador interno de descarte; nunca debe conceder zona.
        var zonas = WebClaims.Zonas(PrincipalConZonas("-1", "-5", "42"));

        Assert.Equal(new[] { 42 }, zonas);
    }

    [Fact]
    public void UserId_ConClaimValido_LoResuelve()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("user_id", "17"),
        }, "test"));

        Assert.Equal(17L, WebClaims.UserId(principal));
    }

    [Theory]
    [InlineData("no-numero")]
    [InlineData("")]
    public void UserId_ConClaimInvalido_DevuelveNulo(string valor)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("user_id", valor),
        }, "test"));

        Assert.Null(WebClaims.UserId(principal));
    }

    [Fact]
    public void UserId_SinClaim_DevuelveNulo()
    {
        Assert.Null(WebClaims.UserId(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Username_ResuelveElNombreYAdmiteAusencia()
    {
        var presente = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("username", "supervisor.centro"),
        }, "test"));

        Assert.Equal("supervisor.centro", WebClaims.Username(presente));
        Assert.Null(WebClaims.Username(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
