using Rutx.Sincronizador.Data;
using Rutx.Sincronizador.Models;
using Rutx.Sincronizador.Tests.Helpers;
using Xunit;

namespace Rutx.Sincronizador.Tests.Unit;

public class ColaOfflineRepositoryTest
{
    [Fact]
    public async Task OperacionProcesandoColgada_SeLiberaYPuedeReintentarse()
    {
        var repo = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        // Operacion PENDIENTE normal
        var normal = new ColaOperacion
        {
            OperacionId = "op-normal",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            Intentos = 0,
            MaxIntentos = 5,
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow.AddMinutes(-10),
            FechaModificacion = DateTime.UtcNow.AddMinutes(-10)
        };
        await repo.InsertarAsync(normal);

        // Operacion "colgada": quedo en PROCESANDO hace mas de 2 minutos
        // (simula que el proceso murio a mitad del procesamiento)
        var colgada = new ColaOperacion
        {
            OperacionId = "op-colgada",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PROCESANDO,
            Intentos = 1,
            MaxIntentos = 5,
            SiguienteReintento = DateTime.UtcNow.AddMinutes(5),
            FechaCreacion = DateTime.UtcNow.AddMinutes(-30),
            FechaModificacion = DateTime.UtcNow.AddMinutes(-30)
        };
        await repo.InsertarAsync(colgada);

        // Operacion en PROCESANDO reciente: NO debe liberarse
        var reciente = new ColaOperacion
        {
            OperacionId = "op-reciente",
            TipoOperacion = TipoOperacion.CLIENTE,
            Payload = "{}",
            Estado = EstadoOperacion.PROCESANDO,
            Intentos = 1,
            MaxIntentos = 5,
            SiguienteReintento = DateTime.UtcNow.AddMinutes(5),
            FechaCreacion = DateTime.UtcNow,
            FechaModificacion = DateTime.UtcNow
        };
        await repo.InsertarAsync(reciente);

        // La colgada debe volver a salir como PENDIENTE (liberada)
        var pendientes = await repo.ObtenerPendientesAsync(limite: 10);
        var ids = pendientes.Select(p => p.OperacionId).ToList();

        Assert.Contains("op-normal", ids);
        Assert.Contains("op-colgada", ids);
        Assert.DoesNotContain("op-reciente", ids);

        var liberada = pendientes.Single(p => p.OperacionId == "op-colgada");
        Assert.Equal(EstadoOperacion.PENDIENTE, liberada.Estado);
        // SiguienteReintento se reajusto a ahora para que el background lo tome
        Assert.True(liberada.SiguienteReintento <= DateTime.UtcNow.AddSeconds(5));
        // Intentos/MaxIntentos se conservan (el liberado no reinicia el contador)
        Assert.Equal(1, liberada.Intentos);
        Assert.Equal(5, liberada.MaxIntentos);
    }

    [Fact]
    public async Task TocarHeartbeat_RefrescaFechaModificacion_DeProcesando()
    {
        var repo = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        var vieja = DateTime.UtcNow.AddMinutes(-30);
        var op = new ColaOperacion
        {
            OperacionId = "op-heartbeat",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PROCESANDO,
            Intentos = 1,
            MaxIntentos = 5,
            SiguienteReintento = DateTime.UtcNow.AddMinutes(5),
            FechaCreacion = vieja,
            FechaModificacion = vieja
        };
        await repo.InsertarAsync(op);

        // La operacion lleva >2 min en PROCESANDO pero el proceso sigue vivo:
        // el heartbeat toca FechaModificacion y evita que se libere.
        await repo.TocarHeartbeatAsync(op.OperacionId);

        var pendientes = await repo.ObtenerPendientesAsync(limite: 10);
        Assert.DoesNotContain(pendientes, p => p.OperacionId == "op-heartbeat");

        // El estado sigue PROCESANDO (no fue liberada)
        var leida = await repo.ObtenerPorIdAsync(op.OperacionId);
        Assert.NotNull(leida);
        Assert.Equal(EstadoOperacion.PROCESANDO, leida!.Estado);
    }
}
