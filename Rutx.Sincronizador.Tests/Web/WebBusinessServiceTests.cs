using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rutx.Sincronizador.Models.Web.Customers;
using Rutx.Sincronizador.Models.Web.Inventory;
using Rutx.Sincronizador.Services.Web;
using Xunit;

namespace Rutx.Sincronizador.Tests.Web;

/// <summary>
/// Pruebas de validación y autorización de zona de los servicios de
/// catálogo e inventario. Se ejecutan SIN tocar Firebird: los casos
/// que requieren la BD real se verifican en la QA del sprint.
/// </summary>
public class WebBusinessServiceTests
{
    private static CustomerWebService CrearClientes()
        => new(ConfigSinDb(), NullLogger<CustomerWebService>.Instance);

    private static InventoryWebService CrearInventario()
        => new(ConfigSinDb(), NullLogger<InventoryWebService>.Instance);

    private static IConfiguration ConfigSinDb() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:FirebirdConnection"] = "User=SYSDBA;Password=x;Database=noexiste.fdb;DataSource=localhost;Port=3050;Dialect=3;Pooling=false;",
        })
        .Build();

    [Fact]
    public async Task Clientes_ZonaFueraDelAlcance_DevuelveFORBIDDENZona()
    {
        var servicio = CrearClientes();

        var resultado = await servicio.ListAsync(
            new CustomerListQuery { ZoneId = 3795 },
            new[] { 3792, 3793 },
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("FORBIDDEN_ZONE", resultado.Code);
    }

    [Fact]
    public async Task Clientes_SinRestriccionDeZonas_NoBloqueaPorZona()
    {
        var servicio = CrearClientes();

        // zone_ids vacíos = alcance total; la consulta avanza (fallará por la BD
        // intencionalmente inexistente, NO por la zona).
        var resultado = await servicio.ListAsync(
            new CustomerListQuery { ZoneId = 3795 },
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("API_UNAVAILABLE", resultado.Code);
    }

    [Fact]
    public async Task Clientes_ConZonasSinPedirZona_NoBloqueaYConsultaAvanza()
    {
        var servicio = CrearClientes();

        // El usuario trae zonas autorizadas pero no filtra por zone_id: la zona
        // se aplica implícitamente (IN @zonas). Con BD inexistente la consulta
        // avanza hasta el intento de conexión → API_UNAVAILABLE (no FORBIDDEN_ZONE).
        var resultado = await servicio.ListAsync(
            new CustomerListQuery(),
            new[] { 3792 },
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("API_UNAVAILABLE", resultado.Code);
    }

    [Fact]
    public async Task Clientes_EstatusInvalido_DevuelveVALIDATION()
    {
        var servicio = CrearClientes();

        var resultado = await servicio.ListAsync(
            new CustomerListQuery { Status = "ZZZ" },
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Inventario_SinRouteId_DevuelveVALIDATION()
    {
        var servicio = CrearInventario();

        var resultado = await servicio.ListByRouteAsync(
            new InventoryRouteQuery(),
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Inventario_AsOfMalFormado_DevuelveVALIDATION()
    {
        var servicio = CrearInventario();

        var resultado = await servicio.ListByRouteAsync(
            new InventoryRouteQuery { RouteId = 695, AsOf = "2026/08" },
            Array.Empty<int>(),
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("VALIDATION_ERROR", resultado.Code);
    }

    [Fact]
    public async Task Inventario_ZonaFueraDelAlcance_DevuelveFORBIDDENZona()
    {
        var servicio = CrearInventario();

        var resultado = await servicio.ListByRouteAsync(
            new InventoryRouteQuery { RouteId = 695, ZoneId = 3795 },
            new[] { 3792 },
            CancellationToken.None);

        Assert.False(resultado.IsSuccess);
        Assert.Equal("FORBIDDEN_ZONE", resultado.Code);
    }
}