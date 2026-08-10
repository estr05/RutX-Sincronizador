using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Services;
using Xunit;

namespace Rutx.Sincronizador.Tests.Integration;

public class LimiteCreditoTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    private static VentaServicePv CrearServicio() =>
        new(Config, LoggerFactory.Create(b => { }).CreateLogger<VentaServicePv>());

    private static UsuarioSesion SesionRuta() => new()
    {
        VendedorId = 9647,
        VendedorNombre = "CAJERORUTA01",
        CajeroId = 9646,
        CajaId = 9712,
        AlmacenId = 9711,
        SucursalId = 4274,
        Usuario = "CAJERORUTA01"
    };

    private static VentaPvCreateDto VentaCredito(int clienteId, decimal unidades, int formaId = 71)
    {
        // Articulo 2115 (GALLETAS) con precio historico de prueba
        return new VentaPvCreateDto
        {
            VentaMovilId = $"TEST-LIMITE-{Guid.NewGuid():N}",
            VendedorId = 9647,
            ClienteId = clienteId,
            FechaHora = DateTime.Now,
            FormaCobroId = formaId,
            Detalles = new List<DetalleVentaPvDto>
            {
                new()
                {
                    ArticuloId = 2115,
                    Unidades = unidades,
                    PrecioUnitario = 7.758621m,
                    ImpuestoId = 622
                }
            }
        };
    }

    [Fact]
    public async Task CreditoQueExcedeLimite_EsRechazado()
    {
        // Cliente 9529 (Abarrotes La Luna): saldo 35.98, limite 5000
        // Venta a credito grande (650 x 7.758621 ~= 5043 + IVA) -> excede por miles
        var servicio = CrearServicio();
        var dto = VentaCredito(9529, 650m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servicio.RegistrarVentaPvAsync(SesionRuta(), dto));

        Assert.Contains("Límite de crédito sobrepasado", ex.Message);
        Assert.Contains("límite $5000.00", ex.Message);
    }

    [Fact]
    public async Task CreditoDeClienteConDeudaHistorica_EsRechazado()
    {
        // Cliente 700 (ALBERT COTA): deuda historica enorme vs limite 10000
        var servicio = CrearServicio();
        var dto = VentaCredito(700, 2m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => servicio.RegistrarVentaPvAsync(SesionRuta(), dto));

        Assert.Contains("Límite de crédito sobrepasado", ex.Message);
        Assert.Contains("límite $10000.00", ex.Message);
    }

    [Fact]
    public async Task CreditoDentroDelLimite_SeRegistra()
    {
        // Cliente 9529: saldo 35.98 + credito pequeno (~9.00) < 5000 -> permitido
        var servicio = CrearServicio();
        var dto = VentaCredito(9529, 1m);

        var respuesta = await servicio.RegistrarVentaPvAsync(SesionRuta(), dto);

        Assert.NotNull(respuesta);
        Assert.True(respuesta.DoctoPvId > 0);

        // Limpieza: cancelar el ticket de prueba
        var cancelada = await servicio.CancelarVentaAsync(respuesta.DoctoPvId);
        Assert.True(cancelada, "El ticket de prueba deberia cancelarse para no contaminar la BD");
    }
}
