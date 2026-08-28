using Rutx.Sincronizador;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Invariantes de topologia de listeners de Kestrel:
///   - Development: 5047 AnyIP (conserva LAN/Tailscale del modo legacy).
///   - Production : 5047 SIEMPRE loopback.
///   - ExternalApiEnabled agrega el listener externo segun modo
///     (ReverseProxy=loopback:5048 para el tunel; KestrelHttps con TLS;
///     VPN_Directo HTTP externo). Modo invalido aborta.
/// </summary>
public class KestrelTopologyTests
{
    private const bool Dev = false;
    private const bool Prod = true;

    [Fact]
    public void Development_Sin_External_Devuelve_Solo_5047_AnyIP()
    {
        var eps = KestrelTopology.ResolveListeners(Dev, externalEnabled: false, mode: null, externalPort: 5048);

        var ep = Assert.Single(eps);
        Assert.False(ep.LoopbackOnly);
        Assert.Equal(5047, ep.Port);
        Assert.False(ep.UseHttps);
    }

    [Fact]
    public void Development_Con_External_ReverseProxy_Agrega_Loopback_5048()
    {
        var eps = KestrelTopology.ResolveListeners(Dev, externalEnabled: true, mode: "ReverseProxy", externalPort: 5048);

        Assert.Equal(2, eps.Count);
        Assert.False(eps[0].LoopbackOnly);
        Assert.Equal(5047, eps[0].Port);
        Assert.True(eps[1].LoopbackOnly);
        Assert.Equal(5048, eps[1].Port);
        Assert.False(eps[1].UseHttps);
    }

    [Fact]
    public void Production_Sin_External_Devuelve_Solo_5047_Loopback()
    {
        var eps = KestrelTopology.ResolveListeners(Prod, externalEnabled: false, mode: null, externalPort: 5048);

        var ep = Assert.Single(eps);
        Assert.True(ep.LoopbackOnly);
        Assert.Equal(5047, ep.Port);
    }

    [Fact]
    public void Production_ReverseProxy_Devuelve_5047_Y_5048_Ambos_Loopback()
    {
        var eps = KestrelTopology.ResolveListeners(Prod, externalEnabled: true, mode: "ReverseProxy", externalPort: 5048);

        Assert.Equal(2, eps.Count);
        Assert.True(eps.All(e => e.LoopbackOnly));
        Assert.Equal(5047, eps[0].Port);
        Assert.Equal(5048, eps[1].Port);
        Assert.DoesNotContain(eps, e => e.UseHttps);
    }

    [Fact]
    public void Production_KestrelHttps_Marca_UseHttps_True()
    {
        var eps = KestrelTopology.ResolveListeners(Prod, externalEnabled: true, mode: "KestrelHttps", externalPort: 9443);

        Assert.Equal(2, eps.Count);
        Assert.True(eps[0].LoopbackOnly);          // admin local intacto
        Assert.False(eps[1].LoopbackOnly);         // externo AnyIP
        Assert.True(eps[1].UseHttps);
        Assert.Equal(9443, eps[1].Port);
    }

    [Fact]
    public void Production_VPN_Directo_Externo_Http_Sin_TLS()
    {
        var eps = KestrelTopology.ResolveListeners(Prod, externalEnabled: true, mode: "VPN_Directo", externalPort: 5048);

        Assert.Equal(2, eps.Count);
        Assert.False(eps[1].LoopbackOnly);
        Assert.False(eps[1].UseHttps);
        Assert.Equal(5048, eps[1].Port);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Modo_Invalido_Lanza_InvalidOperationException(bool esProduction)
    {
        Assert.Throws<InvalidOperationException>(() =>
            KestrelTopology.ResolveListeners(esProduction, externalEnabled: true, mode: "Basura", externalPort: 5048));
    }

    [Fact]
    public void Puerto_Cero_Usa_Default_5048()
    {
        var eps = KestrelTopology.ResolveListeners(Prod, externalEnabled: true, mode: "ReverseProxy", externalPort: 0);

        Assert.Equal(5048, eps[1].Port);
    }
}
