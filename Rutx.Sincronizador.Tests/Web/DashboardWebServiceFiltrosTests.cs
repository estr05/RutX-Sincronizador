using System.Collections.Specialized;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Dapper;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;
using NVCollection = System.Collections.Specialized.NameValueCollection;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Reglas de negocio puras del dashboard, sin tocar Firebird:
/// ventanas temporales por rango, parsing de fechas explÃ­citas,
/// intersecciÃ³n silenciosa de zonas (Â§12.2), filtro por ruta y
/// resoluciÃ³n de formas de cobro crÃ©dito.
/// </summary>
public class DashboardWebServiceFiltrosTests
{
    private static DashboardWebService CrearServicio(Dictionary<string, string?>? extra = null)
    {
        // Claves indexadas: replican la estructura del array JSON en appsettings.
        var config = new Dictionary<string, string?>
        {
            ["MicrosipSettings:CreditFormaCobroIds:0"] = "71",
            ["MicrosipSettings:CreditFormaCobroIds:1"] = "703",
        };
        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
            config[clave] = valor;

        return new DashboardWebService(
            new ConfigurationBuilder().AddInMemoryCollection(config).Build(),
            NullLogger<DashboardWebService>.Instance);
    }

    /// <summary>Lee un parÃ¡metro de lista de DynamicParameters sin depender del tipo concreto.</summary>
    private static T[] ValorLista<T>(DynamicParameters valores, string nombre)
        => valores.Get<System.Collections.IEnumerable>(nombre).Cast<T>().ToArray();

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // CONSTRUIR FILTRO COMÃšN: zonas, ruta y formas de crÃ©dito
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public void Filtro_SiempreAcotaPorFechas()
    {
        var (condiciones, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery(), Array.Empty<int>(), (new(2026, 8, 1), new(2026, 8, 21)), false);

        Assert.Contains(condiciones, c => c.Contains("pv.FECHA >= @desde"));
        Assert.Contains(condiciones, c => c.Contains("pv.FECHA <= @hasta"));
        Assert.Equal(new DateTime(2026, 8, 1), valores.Get<DateTime>("@desde"));
        Assert.Equal(new DateTime(2026, 8, 21), valores.Get<DateTime>("@hasta"));
    }

    [Fact]
    public void Filtro_SinZonasNiZonaPedida_NoRestringeAlcance()
    {
        // Admin sin zonas: alcance total â†’ sin predicado de zona.
        var (condiciones, _) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery(), Array.Empty<int>(), (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.DoesNotContain(condiciones, c => c.Contains("EXISTS"));
    }

    [Fact]
    public void Filtro_ConZonasAsignadas_AplicaInImplicito()
    {
        var (condiciones, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery(), new[] { 3792, 3793 }, (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.Contains(condiciones, c => c.Contains("cz.ZONA_CLIENTE_ID IN @zonas"));
        Assert.Equal(new[] { 3792, 3793 }, ValorLista<int>(valores, "@zonas"));
    }

    [Fact]
    public void Filtro_ZonaPedidaDentroDelAlcance_SeLimitaAElla()
    {
        var (condiciones, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery { ZoneId = 3793 }, new[] { 3792, 3793 },
            (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.Contains(condiciones, c => c.Contains("cz.ZONA_CLIENTE_ID IN @zonas"));
        Assert.Equal(new[] { 3793 }, ValorLista<int>(valores, "@zonas"));
    }

    [Fact]
    public void Filtro_ZonaPedidaFueraDelAlcance_PredicadoFalso()
    {
        // Intersección silenciosa: se inyecta 1=0 para retornar cero resultados.
        var (condiciones, _) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery { ZoneId = 9999 }, new[] { 3792, 3793 },
            (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.Contains("1=0", condiciones);
    }

    [Fact]
    public void Filtro_AdminSinZonas_PuedePedirCualquierZona()
    {
        var (_, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery { ZoneId = 4000 }, Array.Empty<int>(),
            (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.Equal(new[] { 4000 }, ValorLista<int>(valores, "@zonas"));
    }

    [Fact]
    public void Filtro_RutaExplicita_FiltraPorVendedor()
    {
        var (condiciones, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery { RouteId = 118 }, Array.Empty<int>(), (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.Contains(condiciones, c => c.Contains("pv.VENDEDOR_ID = @ruta"));
        Assert.Equal(118, valores.Get<int>("@ruta"));
    }

    [Fact]
    public void Filtro_Resumen_IncluyeFormasCreditoConfiguradas()
    {
        var (_, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery(), Array.Empty<int>(), (new(2026, 8, 1), new(2026, 8, 1)), true);

        Assert.Equal(new[] { 71, 703 }, ValorLista<int>(valores, "@formasCredito"));
    }

    [Fact]
    public void Filtro_Resumen_SinConfigDeCreditos_UsaMarcadorImposible()
    {
        var servicio = new DashboardWebService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            NullLogger<DashboardWebService>.Instance);

        Assert.Equal(new[] { -1 }, servicio.LeerFormasCredito());
    }

    [Fact]
    public void Filtro_Serie_NoNecesitaFormasCredito()
    {
        var (_, valores) = CrearServicio().ConstruirFiltroComun(
            new ReportFilterQuery(), Array.Empty<int>(), (new(2026, 8, 1), new(2026, 8, 1)), false);

        Assert.DoesNotContain("@formasCredito", valores.ParameterNames);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // RESOLUCIÃ“N DE VENTANAS TEMPORALES
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static readonly DateTime Hoy = DateTime.Today;

    [Fact]
    public void VentanaResumen_FechasExplicitas_GananAlDefault()
    {
        var ventana = DashboardWebService.ResolverVentanaResumen(new ReportFilterQuery
        {
            DateFrom = "2026-08-01",
            DateTo = "2026-08-21",
        });

        Assert.Equal(new DateTime(2026, 8, 1), ventana.Desde);
        Assert.Equal(new DateTime(2026, 8, 21), ventana.Hasta);
    }

    [Theory]
    [InlineData(null)]      // sin fechas
    [InlineData("01/08/2026")] // formato fuera del contrato
    public void VentanaResumen_FechasInvalidas_CaenAlDiaEnCurso(string? fecha)
    {
        var ventana = DashboardWebService.ResolverVentanaResumen(new ReportFilterQuery
        {
            DateFrom = fecha,
            DateTo = fecha,
        });

        Assert.Equal(Hoy, ventana.Desde);
        Assert.Equal(Hoy, ventana.Hasta);
    }

    [Fact]
    public void VentanaResumen_RangoSemanal_DesdeElLunes()
    {
        var ventana = DashboardWebService.ResolverVentanaResumen(new ReportFilterQuery { Range = "semanal" });

        Assert.Equal(DashboardWebService.LunesDe(Hoy), ventana.Desde);
        Assert.Equal(Hoy, ventana.Hasta);
    }

    [Fact]
    public void VentanaResumen_RangoMensual_PrimerDiaDelMes()
    {
        var ventana = DashboardWebService.ResolverVentanaResumen(new ReportFilterQuery { Range = "mensual" });

        Assert.Equal(new DateTime(Hoy.Year, Hoy.Month, 1), ventana.Desde);
        Assert.Equal(Hoy, ventana.Hasta);
    }

    [Fact]
    public void VentanaSerie_Mensual_UltimosDoMeses()
    {
        var ventana = DashboardWebService.ResolverVentanaSerie(new ReportFilterQuery(), "mensual");

        Assert.Equal(new DateTime(Hoy.Year, Hoy.Month, 1).AddMonths(-11), ventana.Desde);
        Assert.Equal(Hoy, ventana.Hasta);
    }

    [Fact]
    public void VentanaSerie_Diario_SinFechas_MuestraSoloDiaActual()
    {
        var ventana = DashboardWebService.ResolverVentanaSerie(new ReportFilterQuery(), "diario");
        Assert.Equal(Hoy, ventana.Desde);
        Assert.Equal(Hoy, ventana.Hasta);
    }

    [Fact]
    public void VentanaSerie_Semanal_SinFechas_Ultimos7DiasDesdeHoy()
    {
        var ventana = DashboardWebService.ResolverVentanaSerie(new ReportFilterQuery(), "semanal");
        Assert.Equal(Hoy.AddDays(-6), ventana.Desde);  // HOY-6 a HOY inclusive = 7 días
        Assert.Equal(Hoy, ventana.Hasta);
    }

    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // NORMALIZACIÃ“N Y PARSING
    // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Theory]
    [InlineData("SEMANAL", "semanal")]
    [InlineData(" Mensual ", "mensual")]
    [InlineData("diario", "diario")]
    [InlineData("anual", "diario")]   // rango desconocido â†’ diario
    [InlineData(null, "diario")]
    public void NormalizarRango_SoloAceptaLosTresContratados(string? entrada, string esperado)
        => Assert.Equal(esperado, DashboardWebService.NormalizarRango(entrada));

    [Theory]
    [InlineData("2026-08-21", "2026-08-21")]
    [InlineData("1999-12-31", "1999-12-31")]
    public void ParsearFecha_AceptaSoloFormatoContrato(string entrada, string esperado)
        => Assert.Equal(DateTime.ParseExact(esperado, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DashboardWebService.ParsearFecha(entrada));

    [Theory]
    [InlineData("21-08-2026")]
    [InlineData("")]
    [InlineData(null)]
    public void ParsearFecha_RechazaLoNoContratado(string? entrada)
        => Assert.Null(DashboardWebService.ParsearFecha(entrada));

    [Fact]
    public void LunesDe_AnclaALunesSinImportarElDia()
    {
        var miercoles = new DateTime(2026, 8, 19); // miÃ©rcoles
        var domingo = new DateTime(2026, 8, 23);   // domingo
        var lunesEsperado = new DateTime(2026, 8, 17);

        Assert.Equal(lunesEsperado, DashboardWebService.LunesDe(miercoles));
        Assert.Equal(lunesEsperado, DashboardWebService.LunesDe(domingo));
        Assert.Equal(lunesEsperado, DashboardWebService.LunesDe(lunesEsperado));
    }
}
