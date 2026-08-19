using Rutx.Sincronizador.Models;

namespace Rutx.Sincronizador.Services;

public interface IColaOfflineService
{
    Task<ColaOperacion> AgregarOperacionAsync(TipoOperacion tipo, string idempotencia, object payload);
    Task<ColaOperacion?> ObtenerEstadoAsync(string operacionId);
    Task<List<ColaOperacion>> ObtenerPendientesAsync(int limite = 10);
    Task<List<ColaOperacion>> ReclamarPendientesAsync(int limite = 10, TimeSpan? leaseDuration = null);
    Task<List<ColaOperacion>> ObtenerPorEstadoAsync(EstadoOperacion estado, int limite = 100);
    Task ProcesarOperacionAsync(ColaOperacion operacion);
    Task EliminarAsync(string operacionId);
    Task<int> ContarPorEstadoAsync(EstadoOperacion estado);
    DateTime CalcularSiguienteReintento(int intentos);
}
