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
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow.AddMinutes(-10),
            FechaModificacion = DateTime.UtcNow.AddMinutes(-10)
        };
        await repo.InsertarAsync(normal);

        // Operacion "colgada": PENDIENTE pero con Lease expirado
        var colgada = new ColaOperacion
        {
            OperacionId = "op-colgada",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(-5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow.AddMinutes(-30),
            FechaModificacion = DateTime.UtcNow.AddMinutes(-30)
        };
        await repo.InsertarAsync(colgada);

        // Operacion en "PROCESANDO" reciente (lease activo)
        var reciente = new ColaOperacion
        {
            OperacionId = "op-reciente",
            TipoOperacion = TipoOperacion.CLIENTE,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow,
            FechaModificacion = DateTime.UtcNow
        };
        await repo.InsertarAsync(reciente);

        // Se reclaman las pendientes y las que tienen lease expirado
        var pendientes = await repo.ReclamarPendientesAsync(limite: 10, leaseDuration: TimeSpan.FromMinutes(2));
        var ids = pendientes.Select(p => p.OperacionId).ToList();

        Assert.Contains("op-normal", ids);
        Assert.Contains("op-colgada", ids);
        Assert.DoesNotContain("op-reciente", ids);

        var liberada = pendientes.Single(p => p.OperacionId == "op-colgada");
        Assert.Equal(EstadoOperacion.PENDIENTE, liberada.Estado);
        Assert.True(liberada.LeaseUntil >= DateTime.UtcNow); // Lease fue renovado
        Assert.Equal(1, liberada.Intentos);
        Assert.Equal(5, liberada.MaxIntentos);
    }

    [Fact]
    public async Task TocarHeartbeat_RefrescaLease_DeOperacionReclamada()
    {
        var repo = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        var vieja = DateTime.UtcNow.AddMinutes(-30);
        var op = new ColaOperacion
        {
            OperacionId = "op-heartbeat",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE, // ya no usa PROCESANDO
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = vieja,
            FechaModificacion = vieja
        };
        await repo.InsertarAsync(op);

        // El worker toca el heartbeat, esto debe actualizar LeaseUntil y FechaModificacion
        await repo.TocarHeartbeatAsync(op.OperacionId);

        var pendientes = await repo.ReclamarPendientesAsync(limite: 10);
        // Aún tiene lease vigente, por lo que Reclamar no debe retornarla
        Assert.DoesNotContain(pendientes, p => p.OperacionId == "op-heartbeat");

        var leida = await repo.ObtenerPorIdAsync(op.OperacionId);
        Assert.NotNull(leida);
        Assert.Equal(EstadoOperacion.PENDIENTE, leida!.Estado);
        Assert.NotNull(leida.LeaseUntil);
    }
}
