using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

/// <summary>
/// Validaciones endurecidas de ProductionConfigurationValidator:
///   - Bloque 3b: WebAuth:AdminPassword debil/placeholder se rechaza SIEMPRE
///     en Produccion, INDEPENDIENTEMENTE de Network:ExternalApiEnabled.
///   - Bloque 4b/4c: con API externa activa, AllowedHosts es obligatorio,
///     sin '*' ni placeholders, debe contener hostname publico + host local,
///     y Cloudflare:PublicHostname es OBLIGATORIO y debe estar incluido en
///     AllowedHosts.
/// </summary>
public class ProductionConfigurationValidatorTests
{
    private const string JwtValida = "ClaveDePruebasSuperSecretaConMasDeTreintaYDosCaracteres!!";
    private const string FbValida = "User=RUTX_SYNC;Password=ClaveFb2026!x;Database=C:\\datos\\base.fdb;DataSource=localhost;Port=3050;";
    private const string AdminUser = "admin";
    private const string AdminFuerte = "ClaveAdmin2026!xyz";
    private const string HostPublico = "sync.cliente.com";

    private static Dictionary<string, string?> Base()
    {
        return new Dictionary<string, string?>
        {
            ["Jwt:Key"] = JwtValida,
            ["Jwt:Issuer"] = "RutxSincronizador",
            ["Jwt:Audience"] = "RutxApps",
            ["ConnectionStrings:FirebirdConnection"] = FbValida,
            ["WebAuth:AdminUsername"] = AdminUser,
            ["WebAuth:AdminPassword"] = AdminFuerte,
            ["Network:ExternalApiEnabled"] = "false",
        };
    }

    private static IConfiguration Config(Dictionary<string, string?> valores)
        => new ConfigurationBuilder().AddInMemoryCollection(valores).Build();

    private static IHostEnvironment EntornoProduccion()
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns("Production");
        mock.SetupGet(e => e.ApplicationName).Returns("Rutx.Sincronizador");
        mock.SetupGet(e => e.ContentRootPath).Returns(".");
        return mock.Object;
    }

    private static InvalidOperationException Error(IConfiguration cfg)
    {
        return Assert.Throws<InvalidOperationException>(
            () => Rutx.Sincronizador.Security.ProductionConfigurationValidator.Validate(cfg, EntornoProduccion()));
    }

    // ------------------------------------------------------------------
    // Bloque 3b: AdminPassword SIEMPRE (independencia de ExternalApiEnabled)
    // ------------------------------------------------------------------

    [Fact]
    public void AdminPassword_Ausente_Lanza_Sin_External()
    {
        var d = Base();
        d["WebAuth:AdminPassword"] = null;
        Assert.Contains("AdminUsername y AdminPassword", Error(Config(d)).Message);
    }

    [Theory]
    [InlineData("admin")]                       // literal por defecto
    [InlineData("CHANGE_ME_ADMIN")]             // placeholder del repo saneado
    public void AdminPassword_Debil_Lanza_INCLUSO_Con_ExternalApiEnabled_False(string pass)
    {
        var d = Base();
        d["WebAuth:AdminPassword"] = pass;
        d["Network:ExternalApiEnabled"] = "false"; // independencia explicita
        Assert.Contains("[SEGURIDAD]", Error(Config(d)).Message);
    }

    [Fact]
    public void AdminPassword_Igual_Al_Usuario_Lanza_Incluso_Sin_External()
    {
        var d = Base();
        d["WebAuth:AdminUsername"] = "panelsuper";
        d["WebAuth:AdminPassword"] = "panelsuper";
        d["Network:ExternalApiEnabled"] = "false";
        Assert.Contains("identica al usuario", Error(Config(d)).Message);
    }

    [Fact]
    public void AdminPassword_Corta_Menor_A_8_Lanza_Incluso_Sin_External()
    {
        var d = Base();
        d["WebAuth:AdminPassword"] = "Corta7!";
        d["Network:ExternalApiEnabled"] = "false";
        Assert.Contains("8 caracteres", Error(Config(d)).Message);
    }

    [Fact]
    public void AdminPassword_Exactamente_8_No_Admin_Pasa()
    {
        var d = Base();
        d["WebAuth:AdminPassword"] = "Clave8x!";
        d["Network:ExternalApiEnabled"] = "false";
        var ex = Record.Exception(() => Rutx.Sincronizador.Security.ProductionConfigurationValidator.Validate(Config(d), EntornoProduccion()));
        Assert.Null(ex);
    }

    [Fact]
    public void AdminPassword_Contiene_ChangeMe_Lanza()
    {
        var d = Base();
        d["WebAuth:AdminPassword"] = "ClaveCHANGE_ME!";
        d["Network:ExternalApiEnabled"] = "false";
        Assert.Contains("CHANGE_ME", Error(Config(d)).Message);
    }

    [Fact]
    public void AdminPassword_Fuerte_Pasa_Sin_External()
    {
        var ex = Record.Exception(() => Rutx.Sincronizador.Security.ProductionConfigurationValidator.Validate(Config(Base()), EntornoProduccion()));
        Assert.Null(ex);
    }

    // ------------------------------------------------------------------
    // Bloque 4b/4c: AllowedHosts + PublicHostname con API externa activa
    // ------------------------------------------------------------------

    private static Dictionary<string, string?> External(string? allowedHosts, string? publicHostname, string modo = "ReverseProxy")
    {
        var d = Base();
        d["Network:ExternalApiEnabled"] = "true";
        d["Network:ExternalApiMode"] = modo;
        d["Network:ExternalPort"] = "5048";
        d["AllowedHosts"] = allowedHosts;
        d["Cloudflare:PublicHostname"] = publicHostname;
        return d;
    }

    [Fact]
    public void External_AllowedHosts_Ausente_Lanza()
    {
        Assert.Contains("AllowedHosts explicito", Error(Config(External(null, HostPublico))).Message);
    }

    [Fact]
    public void External_AllowedHosts_Asterisco_Lanza()
    {
        Assert.Contains("AllowedHosts explicito", Error(Config(External("*", HostPublico))).Message);
    }

    [Theory]
    [InlineData("<host>;localhost;127.0.0.1")]
    [InlineData("sync.example.com;localhost;127.0.0.1")]
    public void External_AllowedHosts_Placeholder_Lanza(string hosts)
    {
        Assert.Contains("placeholder", Error(Config(External(hosts, "sync.example.com"))).Message);
    }

    [Fact]
    public void External_Falta_Hostname_Publico_Lanza()
    {
        Assert.Contains("hostname real de Cloudflare", Error(Config(External("localhost;127.0.0.1", HostPublico))).Message);
    }

    [Fact]
    public void External_Falta_Host_Local_Lanza()
    {
        Assert.Contains("hostname real de Cloudflare", Error(Config(External($"{HostPublico};otro.cliente.com", HostPublico))).Message);
    }

    [Fact]
    public void External_PublicHostname_Ausente_Lanza()
    {
        Assert.Contains("Cloudflare__PublicHostname", Error(Config(External($"{HostPublico};localhost;127.0.0.1", null))).Message);
    }

    [Fact]
    public void External_PublicHostname_No_Incluido_En_AllowedHosts_Lanza()
    {
        Assert.Contains("debe estar incluido en AllowedHosts",
            Error(Config(External($"otrohost.com;localhost;127.0.0.1", HostPublico))).Message);
    }

    [Fact]
    public void External_Configuracion_Valida_Pasa()
    {
        var ex = Record.Exception(() =>
            Rutx.Sincronizador.Security.ProductionConfigurationValidator.Validate(
                Config(External($"{HostPublico};localhost;127.0.0.1", HostPublico)), EntornoProduccion()));
        Assert.Null(ex);
    }

    [Fact]
    public void External_PublicHostname_Placeholder_Lanza()
    {
        Assert.Contains("placeholder",
            Error(Config(External("<sync.example.com>;localhost;127.0.0.1", "<sync.example.com>"))).Message);
    }

    [Fact]
    public void External_PublicHostname_Es_IP_Lanza()
    {
        var ex = Error(Config(External("203.0.113.10;localhost;127.0.0.1", "203.0.113.10")));
        Assert.Contains("nombre de dominio publico real", ex.Message);
    }

    [Fact]
    public void External_PublicHostname_Es_Localhost_Lanza()
    {
        var ex = Error(Config(External("sync.cliente.com;localhost;127.0.0.1", "localhost")));
        Assert.Contains("nombre de dominio publico real", ex.Message);
    }
}
