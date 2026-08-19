using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Data;

public interface IColaOfflineRepository
{
    Task<ColaOperacion> InsertarAsync(ColaOperacion operacion);
    Task<ColaOperacion?> ObtenerPorIdAsync(string operacionId);
    Task<List<ColaOperacion>> ObtenerPendientesAsync(int limite = 10);
    Task<List<ColaOperacion>> ReclamarPendientesAsync(int limite = 10, TimeSpan? leaseDuration = null);
    Task<List<ColaOperacion>> ObtenerPorEstadoAsync(EstadoOperacion estado, int limite = 100);
    Task ActualizarAsync(ColaOperacion operacion);
    Task EliminarAsync(string operacionId);
    Task<int> ContarPorEstadoAsync(EstadoOperacion estado);
    Task<ColaOperacion?> ObtenerPorIdempotenciaAsync(TipoOperacion tipo, string idempotencia);

    /// <summary>
    /// Heartbeat: refresca FechaModificacion de una operacion en PROCESANDO
    /// para que ObtenerPendientesAsync no la libere como "colgada" mientras
    /// todavia se esta procesando (procesamiento lento + retries).
    /// </summary>
    Task TocarHeartbeatAsync(string operacionId);

    /// <summary>Registros de idempotencia de ventas (evita duplicados en reintentos).</summary>
    Task<VentaSincronizada?> ObtenerVentaSincronizadaAsync(string ventaMovilId);

    /// <summary>Reserva un marcador PROCESANDO. Devuelve false si ya existe.</summary>
    Task<bool> ReservarVentaAsync(string ventaMovilId);

    /// <summary>Marca la venta como COMPLETADO con su folio/ID de Microsip.</summary>
    Task CompletarVentaAsync(string ventaMovilId, int doctoPvId, string folio);

    /// <summary>Libera el marcador (fallo transitorio) para poder reintentar.</summary>
    Task LiberarVentaAsync(string ventaMovilId);
}
