using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

/// <summary>
/// Reconciliacion de movimientos de inventario (DOCTOS_IN / DOCTOS_IN_DET).
/// Clasifica cada operacion como TRANSFER (par S/E de traspaso), SEED
/// (saldo inicial sin pareja) o ANOMALIA (movimiento sin pareja o que
/// viola la regla de carga del coche), y la reporta.
/// </summary>
public interface IInventarioReconciliacionService
{
    /// <summary>
    /// Ejecuta la reconciliacion desde [desde] y retorna el resultado
    /// (operaciones clasificadas + anomalias).
    /// </summary>
    Task<ResultadoReconciliacionDto> ReconciliarAsync(DateTime desde);

    /// <summary>Ultimo resultado de una corrida (la alimenta el servicio de fondo).</summary>
    ResultadoReconciliacionDto? UltimoResultado { get; }
}
