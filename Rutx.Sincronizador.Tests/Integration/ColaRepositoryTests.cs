using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Tests.Helpers;
using Xunit;

namespace Rutx.Sincronizador.Tests.Integration;

[Trait("Category", "Integration")]
public class ColaRepositoryTests
{
    [Fact]
    public async Task InsertarOperacion_Y_Consultar()
    {
        var repository = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        var operacion = new ColaOperacion
        {
            OperacionId = "test-001",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{\"test\": true}",
            Estado = EstadoOperacion.PENDIENTE
        };

        var insertada = await repository.InsertarAsync(operacion);
        var consultada = await repository.ObtenerPorIdAsync("test-001");

        Assert.NotNull(consultada);
        Assert.Equal(insertada.Id, consultada!.Id);
        Assert.Equal("test-001", consultada.OperacionId);
    }

    [Fact]
    public async Task ActualizarEstado_Y_Verificar()
    {
        var repository = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        var operacion = new ColaOperacion
        {
            OperacionId = "test-002",
            TipoOperacion = TipoOperacion.CLIENTE,
            Payload = "{\"test\": true}",
            Estado = EstadoOperacion.PENDIENTE
        };

        await repository.InsertarAsync(operacion);

        operacion.Estado = EstadoOperacion.COMPLETADO;
        operacion.Intentos = 1;
        await repository.ActualizarAsync(operacion);

        var consultada = await repository.ObtenerPorIdAsync("test-002");

        Assert.NotNull(consultada);
        Assert.Equal(EstadoOperacion.COMPLETADO, consultada!.Estado);
        Assert.Equal(1, consultada.Intentos);
    }

    [Fact]
    public async Task ObtenerPendientes_Ordenadas()
    {
        var repository = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        await repository.InsertarAsync(new ColaOperacion
        {
            OperacionId = "orden-2",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            FechaCreacion = DateTime.UtcNow.AddMinutes(2)
        });

        await repository.InsertarAsync(new ColaOperacion
        {
            OperacionId = "orden-1",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            FechaCreacion = DateTime.UtcNow.AddMinutes(1)
        });

        var pendientes = await repository.ObtenerPendientesAsync();

        Assert.Equal(2, pendientes.Count);
        Assert.Equal("orden-1", pendientes[0].OperacionId);
        Assert.Equal("orden-2", pendientes[1].OperacionId);
    }

    [Fact]
    public async Task EliminarCompletadas()
    {
        var repository = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        await repository.InsertarAsync(new ColaOperacion
        {
            OperacionId = "eliminar-001",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.COMPLETADO
        });

        await repository.EliminarAsync("eliminar-001");
        var consultada = await repository.ObtenerPorIdAsync("eliminar-001");

        Assert.Null(consultada);
    }

    [Fact]
    public async Task ContarPorEstado_ConteoCorrecto()
    {
        var repository = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        await repository.InsertarAsync(new ColaOperacion
        {
            OperacionId = "conteo-1",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE
        });

        await repository.InsertarAsync(new ColaOperacion
        {
            OperacionId = "conteo-2",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.COMPLETADO
        });

        var pendientes = await repository.ContarPorEstadoAsync(EstadoOperacion.PENDIENTE);
        var completados = await repository.ContarPorEstadoAsync(EstadoOperacion.COMPLETADO);

        Assert.Equal(1, pendientes);
        Assert.Equal(1, completados);
    }
}
