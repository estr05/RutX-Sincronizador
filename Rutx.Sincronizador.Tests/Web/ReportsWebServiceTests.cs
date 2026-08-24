using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Models.Web;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

public class ReportsWebServiceTests
{
    [Fact]
    public async Task ObtenerReporteVentasAsync_SinFirebird_LanzaExcepcion()
    {
        // ARRANGE
        // Limitaci�n documentada: no podemos ejecutar pruebas unitarias del reporte
        // completo contra Firebird porque requerir�a una base de datos real con
        // metadatos de ventas pre-poblados. Por dise�o (seg�n instrucciones),
        // NO simulamos datos con mocks de Dapper para declarar el backend validado.

        var configPairs = new Dictionary<string, string?>
        {
            {"WebFeatures:Reports", "true"},
            {"ConnectionStrings:FirebirdConnection", "User=SYSDBA;Password=masterkey;Database=TEST.FDB;DataSource=localhost;Port=3050;Dialect=3;Charset=UTF8;Role=;Connection lifetime=15;Pooling=true;MinPoolSize=0;MaxPoolSize=50;Packet Size=8192;ServerType=0;"}
        };

        var config = new ConfigurationBuilder().AddInMemoryCollection(configPairs).Build();
        var service = new ReportsWebService(config, NullLogger<ReportsWebService>.Instance);
        var filtros = new ReportFilterQuery { Range = "mensual" };

        // ACT & ASSERT
        // La conexi�n a TEST.FDB fallar� porque no es una BD de Firebird accesible/poblada localmente.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.ObtenerReporteVentasAsync(filtros, Array.Empty<int>()));

        // Esto verifica que el flujo llega hasta la BD y falla ah� por limitaci�n de test real
        Assert.Contains("Error", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CalcularTotales_SumaCadaMontoDesdeByRoute()
    {
        var byRoute = new List<RouteSalesAggregateDto>
        {
            new() { RouteName = "Ruta Norte", Pieces = 5, CashAmount = 400m, CreditAmount = 200m, TotalAmount = 600m },
            new() { RouteName = "Ruta Sur",   Pieces = 2, CashAmount = 10m,  CreditAmount = 0m,  TotalAmount = 10m },
        };

        var totales = ReportsWebService.CalcularTotales(byRoute);

        Assert.Equal(410m, totales.CashAmount);
        Assert.Equal(200m, totales.CreditAmount);
        Assert.Equal(610m, totales.TotalAmount);
        Assert.Equal(610m, totales.SalesAmount);   // sales_amount ≡ total_amount (misma fuente)
        Assert.Equal(7, totales.Pieces);
        Assert.Equal("MXN", totales.Currency);
    }

    [Fact]
    public void CalcularTotales_ListaVacia_DevuelveCerosSinInventarDatos()
    {
        var totales = ReportsWebService.CalcularTotales(new List<RouteSalesAggregateDto>());

        Assert.Equal(0m, totales.SalesAmount);
        Assert.Equal(0m, totales.CashAmount);
        Assert.Equal(0m, totales.CreditAmount);
        Assert.Equal(0m, totales.TotalAmount);
        Assert.Equal(0, totales.Pieces);
        Assert.Equal("MXN", totales.Currency);
    }

    [Fact]
    public void CalcularTotales_ContadoMasCredito_IgualaTotalPorRutaYEnSuma()
    {
        var byRoute = new List<RouteSalesAggregateDto>
        {
            new() { RouteName = "A", Pieces = 3, CashAmount = 100.50m, CreditAmount = 49.50m, TotalAmount = 150.00m },
            new() { RouteName = "B", Pieces = 1, CashAmount = 0m,      CreditAmount = 25.25m, TotalAmount = 25.25m },
        };

        var totales = ReportsWebService.CalcularTotales(byRoute);

        foreach (var fila in byRoute)
            Assert.Equal(fila.TotalAmount, fila.CashAmount + fila.CreditAmount);

        Assert.Equal(totales.TotalAmount, totales.CashAmount + totales.CreditAmount);
    }
}
