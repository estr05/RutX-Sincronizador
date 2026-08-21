using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Comportamiento observable del DashboardWebService sin tocar Firebird:
/// gating por feature flag, validación de configuración y avance de la
/// consulta según el alcance de zonas (la intersección silenciosa no
/// bloquea: el fallo llega del intento de conexión, nunca de la zona).
/// </summary>
public class DashboardWebServiceTests
{
    private const string CadenaPuertoCerrado =
        "User=SYSDBA;Password=x;Database=noexiste.fdb;DataSource=localhost;Port=3051;Dialect=3;Pooling=false;";

    private static DashboardWebService CrearServicio(Dictionary<string, string?> config)
        => new(new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            NullLogger<DashboardWebService>.Instance);

    private static Dictionary<string, string?> Config(Dictionary<string, string?> extra)
    {
        var baseConfig = new Dictionary<string, string?>
        {
            ["ConnectionStrings:FirebirdConnection"] = CadenaPuertoCerrado,
        };
        foreach (var (clave, valor) in extra)
            baseConfig[clave] = valor;
        return baseConfig;
    }

    [Fact]
    public async Task Resumen_FlagDeshabilitado_LanzaFeatureNotReady()
    {
        var servicio = CrearServicio(Config(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "false",
        }));

        var ex = await Assert.ThrowsAsync<FeatureNotReadyException>(() =>
            servicio.ObtenerResumenAsync(new ReportFilterQuery(), Array.Empty<int>(), CancellationToken.None));

        Assert.Contains("Dashboard", ex.Message);
    }

    [Fact]
    public async Task Serie_FlagDeshabilitado_LanzaFeatureNotReady()
    {
        var servicio = CrearServicio(Config(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "false",
        }));

        await Assert.ThrowsAsync<FeatureNotReadyException>(() =>
            servicio.ObtenerSerieVentasAsync(new ReportFilterQuery(), Array.Empty<int>(), CancellationToken.None));
    }

    [Fact]
    public async Task Resumen_SinCadenaDeConexion_LanzaInstruccionesClaras()
    {
        var servicio = CrearServicio(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "true",
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            servicio.ObtenerResumenAsync(new ReportFilterQuery(), Array.Empty<int>(), CancellationToken.None));

        Assert.Contains("FirebirdConnection", ex.Message);
    }

    [Fact]
    public async Task Resumen_ZonasVacias_AvanzanHastaLaConexion()
    {
        // zone_ids vacíos = alcance total: sin filtro de zona; el fallo es de BD.
        var servicio = CrearServicio(Config(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "true",
        }));

        var ex = await Record.ExceptionAsync(() =>
            servicio.ObtenerResumenAsync(new ReportFilterQuery(), Array.Empty<int>(), CancellationToken.None));

        Assert.NotNull(ex);
        Assert.IsNotType<FeatureNotReadyException>(ex);
    }

    [Fact]
    public async Task Resumen_ZonasAsignadas_AplicadasImplicitamenteNoBloquean()
    {
        // Con zonas autorizadas la consulta avanza (IN @zonas implícito):
        // el fallo sigue siendo de conexión, no de autorización.
        var servicio = CrearServicio(Config(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "true",
        }));

        var ex = await Record.ExceptionAsync(() =>
            servicio.ObtenerResumenAsync(new ReportFilterQuery(), new[] { 3792 }, CancellationToken.None));

        Assert.NotNull(ex);
        Assert.IsNotType<FeatureNotReadyException>(ex);
    }

    [Fact]
    public async Task Serie_ZonasVacias_AvanzanHastaLaConexion()
    {
        var servicio = CrearServicio(Config(new Dictionary<string, string?>
        {
            ["WebFeatures:Dashboard"] = "true",
        }));

        var ex = await Record.ExceptionAsync(() =>
            servicio.ObtenerSerieVentasAsync(new ReportFilterQuery { Range = "mensual" },
                Array.Empty<int>(), CancellationToken.None));

        Assert.NotNull(ex);
        Assert.IsNotType<FeatureNotReadyException>(ex);
    }
}
