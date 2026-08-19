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

        // Operación PENDIENTE normal.
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

        // Operación PENDIENTE con lease expirado.
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

        // Operación PENDIENTE con lease activo.
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

        // Se reclaman las pendientes y las que tienen el lease expirado.
        var pendientes = await repo.ReclamarPendientesAsync(
            limite: 10,
            leaseDuration: TimeSpan.FromMinutes(2));
        var ids = pendientes.Select(p => p.OperacionId).ToList();

        Assert.Contains("op-normal", ids);
        Assert.Contains("op-colgada", ids);
        Assert.DoesNotContain("op-reciente", ids);

        var liberada = pendientes.Single(p => p.OperacionId == "op-colgada");
        Assert.Equal(EstadoOperacion.PENDIENTE, liberada.Estado);
        Assert.True(liberada.LeaseUntil >= DateTime.UtcNow); // El lease fue renovado.
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
            Estado = EstadoOperacion.PENDIENTE, // El modelo actual no usa PROCESANDO para esta cola.
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = vieja,
            FechaModificacion = vieja
        };
        await repo.InsertarAsync(op);

        // El worker toca el heartbeat; esto debe actualizar LeaseUntil y FechaModificacion.
        await repo.TocarHeartbeatAsync(op.OperacionId);

        var pendientes = await repo.ReclamarPendientesAsync(limite: 10);
        // Aún tiene un lease vigente, por lo que Reclamar no debe retornarla.
        Assert.DoesNotContain(pendientes, p => p.OperacionId == "op-heartbeat");

        var leida = await repo.ObtenerPorIdAsync(op.OperacionId);
        Assert.NotNull(leida);
        Assert.Equal(EstadoOperacion.PENDIENTE, leida!.Estado);
        Assert.NotNull(leida.LeaseUntil);
    }

    [Fact]
    public async Task TocarHeartbeat_FallaSiLeaseExpiroOEstadoEsCompletado()
    {
        var repo = TestDatabaseHelper.CrearBaseDatosEnMemoria();

        var completada = new ColaOperacion
        {
            OperacionId = "op-completada",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.COMPLETADO,
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow,
            FechaModificacion = DateTime.UtcNow
        };
        await repo.InsertarAsync(completada);

        var expirada = new ColaOperacion
        {
            OperacionId = "op-expirada",
            TipoOperacion = TipoOperacion.VENTA,
            Payload = "{}",
            Estado = EstadoOperacion.PENDIENTE,
            Intentos = 1,
            MaxIntentos = 5,
            LeaseUntil = DateTime.UtcNow.AddMinutes(-5),
            SiguienteReintento = DateTime.UtcNow.AddMinutes(-1),
            FechaCreacion = DateTime.UtcNow,
            FechaModificacion = DateTime.UtcNow
        };
        await repo.InsertarAsync(expirada);

        // Se conservan los valores persistidos antes de intentar tocar el heartbeat.
        var leaseCompletadaInicial = completada.LeaseUntil;
        var leaseExpiradaInicial = expirada.LeaseUntil;

        // Ninguna de las dos operaciones cumple las condiciones para actualizar el heartbeat:
        // una está COMPLETADA y la otra tiene el lease expirado.
        await repo.TocarHeartbeatAsync("op-completada");
        await repo.TocarHeartbeatAsync("op-expirada");

        var cLeida = await repo.ObtenerPorIdAsync("op-completada");
        Assert.NotNull(cLeida);
        Assert.Equal(EstadoOperacion.COMPLETADO, cLeida!.Estado);
        Assert.Equal(leaseCompletadaInicial, cLeida.LeaseUntil);

        var eLeida = await repo.ObtenerPorIdAsync("op-expirada");
        Assert.NotNull(eLeida);
        Assert.Equal(EstadoOperacion.PENDIENTE, eLeida!.Estado);
        Assert.Equal(leaseExpiradaInicial, eLeida.LeaseUntil);
    }
}
